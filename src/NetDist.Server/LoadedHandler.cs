﻿using Microsoft.CSharp;
using NCrontab;
using NetDist.Core;
using NetDist.Core.Extensions;
using NetDist.Core.Utilities;
using NetDist.Handlers;
using NetDist.Jobs;
using NetDist.Jobs.DataContracts;
using NetDist.Logging;
using NetDist.Server.XDomainObjects;
using System;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetDist.Server
{
    /// <summary>
    /// Represents a handler which is loaded and active
    /// This object runs in it's own domain
    /// </summary>
    public class LoadedHandler : MarshalByRefObject
    {
        /// <summary>
        /// Logger object
        /// </summary>
        public Logger Logger { get; set; }

        /// <summary>
        /// ID of the loaded handler
        /// </summary>
        public Guid Id { get; private set; }

        /// <summary>
        /// Current state of the handler
        /// </summary>
        public HandlerState HandlerState { get; private set; }

        /// <summary>
        /// Flag to check if there are available jobs
        /// </summary>
        public bool HasAvailableJobs
        {
            get { return HandlerState == HandlerState.Running && !_availableJobs.IsEmpty; }
        }

        /// <summary>
        /// Full name of the handler: PluginName/HandlerName/JobName
        /// </summary>
        public string FullName
        {
            get { return String.Format("{0}/{1}/{2}", _jobScriptFile.PackageName, _handlerSettings.HandlerName, _handlerSettings.JobName); }
        }

        /// <summary>
        /// Time when the handler was last started
        /// </summary>
        public DateTime? LastStartTime { get; set; }

        /// <summary>
        /// Time when the handler will start next time
        /// </summary>
        private DateTime? NextStartTime { get; set; }

        /// <summary>
        /// Instance of the effective handler
        /// </summary>
        private IHandler _handler;

        /// <summary>
        /// Instance of handler settings
        /// </summary>
        private HandlerSettings _handlerSettings;

        /// <summary>
        /// Queue for the available jobs
        /// </summary>
        private ConcurrentQueue<JobWrapper> _availableJobs;

        /// <summary>
        /// List for jobs which are in progress
        /// </summary>
        private Dictionary<Guid, JobWrapper> _pendingJobs;

        /// <summary>
        /// List for jobs which are finished and waiting to be collected
        /// </summary>
        private ConcurrentQueue<JobWrapper> _finishedJobs;

        private long _totalProcessedJobs;
        private long _totalFailedJobs;

        /// <summary>
        /// Object used for stuff that should be thread-safe
        /// </summary>
        private readonly object _lockObject = new object();

        private readonly JobScriptFile _jobScriptFile;
        private readonly string _currentPackageFolder;
        private string _jobAssemblyPath;
        private Task _schedulerTask;
        private Task _controlTask;
        private CancellationTokenSource _schedulerTaskCancelToken = new CancellationTokenSource();
        private CancellationTokenSource _controlTaskCancelToken = new CancellationTokenSource();
        private readonly AutoResetEvent _jobsEmptyWaitHandle = new AutoResetEvent(false);
        private readonly AutoResetEvent _resultAvailableWaitHandle = new AutoResetEvent(false);
        private CrontabSchedule _cronSchedule;

        /// <summary>
        /// Constructor
        /// </summary>
        public LoadedHandler(JobScriptFile jobScriptFile, string packageBaseFolder)
        {
            // Initialization
            Id = Guid.NewGuid();
            _jobScriptFile = jobScriptFile;
            _currentPackageFolder = Path.Combine(packageBaseFolder, jobScriptFile.PackageName);
            Logger = new Logger();
            _availableJobs = new ConcurrentQueue<JobWrapper>();
            _pendingJobs = new Dictionary<Guid, JobWrapper>();
            _finishedJobs = new ConcurrentQueue<JobWrapper>();
            HandlerState = HandlerState.Stopped;
        }

        /// <summary>
        /// Lifetime override of the proxy object
        /// </summary>
        public override object InitializeLifetimeService()
        {
            // Infinite lifetime
            return null;
        }

        /// <summary>
        /// Initializes the handler and everything it needs to run
        /// </summary>
        public JobHandlerInitializeResult Initialize()
        {
            // Preparations
            var result = new JobHandlerInitializeResult
            {
                PackageName = _jobScriptFile.PackageName
            };

            // Prepare compiler
            var codeProvider = new CSharpCodeProvider();
            var options = new CompilerParameters
            {
                GenerateInMemory = false,
                OutputAssembly = Path.Combine(_currentPackageFolder, String.Format("_job_{0}.dll", HashCalculator.CalculateMd5Hash(_jobScriptFile.JobScript))),
                IncludeDebugInformation = true,
                CompilerOptions = String.Format("/lib:\"{0}\"", _currentPackageFolder)
            };
            // Add libraries
            foreach (var library in _jobScriptFile.CompilerLibraries)
            {
                options.ReferencedAssemblies.Add(library);
            }
            // Compile it
            var compilerResults = codeProvider.CompileAssemblyFromSource(options, _jobScriptFile.JobScript);
            if (compilerResults.Errors.HasErrors)
            {
                var sbOutput = new StringBuilder();
                for (int i = 0; i < compilerResults.Output.Count; i++)
                {
                    sbOutput.AppendLine(compilerResults.Output[i]);
                }
                result.CompileOutput = sbOutput.ToString();
                var sbError = new StringBuilder();
                for (int i = 0; i < compilerResults.Errors.Count; i++)
                {
                    sbError.AppendFormat("{0}: {1}", i, compilerResults.Errors[i]).AppendLine();
                }
                var errorString = sbError.ToString();
                Logger.Error("Failed to compile job script: {0}", errorString);
                // Fill result object
                result.SetError(AddJobHandlerErrorReason.CompilationFailed, errorString);
                return result;
            }
            _jobAssemblyPath = compilerResults.PathToAssembly;
            result.JobAssemblyPath = _jobAssemblyPath;

            // Instantiate the job to get out the settings
            var jobAssembly = AppDomain.CurrentDomain.Load(AssemblyName.GetAssemblyName(_jobAssemblyPath));
            // Search for the initializer
            Type jobInitializerType = null;
            foreach (var type in jobAssembly.GetTypes())
            {
                if (typeof(IJobHandlerInitializer).IsAssignableFrom(type))
                {
                    jobInitializerType = type;
                    break;
                }
            }
            if (jobInitializerType == null)
            {
                result.SetError(AddJobHandlerErrorReason.JobInitializerMissing, "Job initializer type not found");
                return result;
            }
            // Initialize the job
            var jobInstance = (IJobHandlerInitializer)Activator.CreateInstance(jobInitializerType);
            // Read the settings
            _handlerSettings = jobInstance.GetHandlerSettings();
            var customSettings = jobInstance.GetCustomHandlerSettings();

            // Add new information
            result.HandlerName = _handlerSettings.HandlerName;
            result.JobName = _handlerSettings.JobName;

            // Initialize the handler
            var pluginPath = Path.Combine(_currentPackageFolder, String.Format("{0}.dll", _jobScriptFile.PackageName));
            var handlerAssembly = AppDomain.CurrentDomain.Load(AssemblyName.GetAssemblyName(pluginPath));

            // Try loading the types
            Type[] types;
            try
            {
                types = handlerAssembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                result.SetError(AddJobHandlerErrorReason.TypeException, ex.LoaderExceptions.First().Message);
                return result;
            }

            Type typeToLoad = null;
            foreach (var type in types)
            {
                if (typeof(IHandler).IsAssignableFrom(type))
                {
                    var att = type.GetCustomAttribute<HandlerNameAttribute>(true);
                    if (att != null)
                    {
                        if (att.HandlerName == _handlerSettings.HandlerName)
                        {
                            typeToLoad = type;
                            break;
                        }
                    }
                }
            }
            if (typeToLoad == null)
            {
                result.SetError(AddJobHandlerErrorReason.JobHandlerMissing, String.Format("Handler type for handler '{0}' not found", _handlerSettings.HandlerName));
                return result;
            }
            var handlerInstance = (IHandler)Activator.CreateInstance(typeToLoad);
            // Initialize the handler with the custom settings
            handlerInstance.InitializeCustomSettings(customSettings);
            // Call the virtual initialize method
            handlerInstance.Initialize();
            // Event when a job was added
            handlerInstance.EnqueueJobEvent += EnqueueJob;
            // Assign the handler
            _handler = handlerInstance;

            // Initialize cron scheduler
            _cronSchedule = null;
            NextStartTime = null;
            if (!String.IsNullOrWhiteSpace(_handlerSettings.Schedule))
            {
                try
                {
                    _cronSchedule = CrontabSchedule.Parse(_handlerSettings.Schedule);
                    NextStartTime = _cronSchedule.GetNextOccurrence(DateTime.Now);
                }
                catch (Exception ex)
                {
                    Logger.Warn("Failed to parse Crontab: '{0}' - Ex: {1}", _handlerSettings.Schedule, ex.Message);
                }
            }

            // Small task to regularly check for a scheduled start
            if (_cronSchedule != null)
            {
                _schedulerTask = Task.Factory.StartNew(() =>
                {
                    while (!_schedulerTaskCancelToken.IsCancellationRequested)
                    {
                        if (NextStartTime < DateTime.Now)
                        {
                            lock (_lockObject)
                            {
                                if (HandlerState != HandlerState.Running)
                                {
                                    StartJobHandler();
                                    NextStartTime = _cronSchedule.GetNextOccurrence(DateTime.Now);
                                }
                            }
                        }
                        Thread.Sleep(5000);
                    }
                }, _schedulerTaskCancelToken.Token);
            }

            // Autostart if wanted
            if (_handlerSettings.AutoStart)
            {
                StartJobHandler();
            }

            // Fill and return the info object
            result.HandlerId = Id;
            return result;
        }

        /// <summary>
        /// Clear all resources
        /// </summary>
        public void Shutdown()
        {
            if (_schedulerTask != null)
            {
                _schedulerTaskCancelToken.Cancel();
                // Wait until the task is finished (but not when faulted)
                if (!_schedulerTask.IsFaulted)
                {
                    _schedulerTask.Wait();
                }
            }
        }

        /// <summary>
        /// Get information about this handler
        /// </summary>
        public HandlerInfo GetInfo()
        {
            var hInfo = new HandlerInfo
            {
                Id = Id,
                PluginName = _jobScriptFile.PackageName,
                HandlerName = _handlerSettings.HandlerName,
                JobName = _handlerSettings.JobName,
                JobsAvailable = _availableJobs.Count,
                JobsPending = _pendingJobs.Count,
                TotalJobsProcessed = Interlocked.Read(ref _totalProcessedJobs),
                TotalJobsFailed = Interlocked.Read(ref _totalFailedJobs),
                HandlerState = HandlerState,
                LastStartTime = LastStartTime,
                NextStartTime = NextStartTime
            };
            // Calculate the total job count
            hInfo.TotalJobsAvailable = _handler.GetTotalJobCount();
            if (hInfo.TotalJobsAvailable < 0)
            {
                // Set it to the current available jobs if it is unknown
                hInfo.TotalJobsAvailable = hInfo.JobsAvailable;
            }
            return hInfo;
        }

        public HandlerJobInfo GetJobInfo()
        {
            var hInfo = new HandlerJobInfo
            {
                HandlerName = FullName,
                JobAssemblyName = Path.GetFileName(_jobAssemblyPath),
                Depdendencies = new List<string>(_jobScriptFile.Dependencies)
            };
            return hInfo;
        }

        public byte[] GetFile(string file)
        {
            var fullPath = Path.Combine(_currentPackageFolder, file);
            if (!File.Exists(fullPath)) { return null; }
            var content = File.ReadAllBytes(fullPath);
            return content;
        }

        /// <summary>
        /// Starts the job handler so jobs are generated and processed
        /// </summary>
        public void StartJobHandler()
        {
            lock (_lockObject)
            {
                // Check if the control thread is not yet running
                if (_controlTask == null)
                {
                    // Start the control task
                    _controlTaskCancelToken = new CancellationTokenSource();
                    _controlTask = new Task(ControlThread, _controlTaskCancelToken.Token);
                    _controlTask.ContinueWith(t =>
                    {
                        Logger.Error(t.Exception, "Exception in handler '{0}'", Id);
                        StopJobHandler();
                    }, TaskContinuationOptions.OnlyOnFaulted);
                    HandlerState = HandlerState.Running;
                    _controlTask.Start();
                    LastStartTime = DateTime.Now;
                }
            }
        }

        /// <summary>
        /// Stops the job handler
        /// </summary>
        public bool StopJobHandler()
        {
            lock (_lockObject)
            {
                // Check if the control thread is running
                if (_controlTask != null)
                {
                    // Notify the task to stop
                    _controlTaskCancelToken.Cancel();
                    // Wait until the task is finished (but not when faulted)
                    if (!_controlTask.IsFaulted)
                    {
                        _controlTask.Wait();
                    }
                    // Set the state to stopped
                    HandlerState = HandlerState.Stopped;
                    // Reset the control task
                    _controlTask = null;
                    // Clear the various queues/lists/stats
                    _availableJobs = new ConcurrentQueue<JobWrapper>();
                    lock (_pendingJobs.GetSyncRoot())
                    {
                        _pendingJobs = new Dictionary<Guid, JobWrapper>();
                    }
                    _finishedJobs = new ConcurrentQueue<JobWrapper>();
                    Interlocked.Exchange(ref _totalProcessedJobs, 0);
                    Interlocked.Exchange(ref _totalFailedJobs, 0);
                    // Signal the handler to stop
                    _handler.OnStop();
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Gets the next job from the available queue
        /// </summary>
        public Job GetNextJob(Guid clientId)
        {
            JobWrapper assignedJob;
            var success = _availableJobs.TryDequeue(out assignedJob);
            if (success)
            {
                // Set the assigned values
                assignedJob.AssignedTime = DateTime.Now;
                assignedJob.AssignedCliendId = clientId;
                // Add it to pending jobs
                lock (_pendingJobs.GetSyncRoot())
                {
                    _pendingJobs[assignedJob.Id] = assignedJob;
                }
                // Check if more jobs are available
                if (_availableJobs.IsEmpty)
                {
                    // If not, set the waithandle to get new jobs
                    _jobsEmptyWaitHandle.Set();
                }
                return assignedJob.CreateJob();
            }
            return null;
        }

        public bool ReceivedResult(JobResult result)
        {
            // Catch case where we receive results for an already stopped handler
            if (HandlerState == HandlerState.Stopped)
            {
                Logger.Warn("Got job '{0}' result for stopped handler", result.JobId);
                return false;
            }

            lock (_pendingJobs.GetSyncRoot())
            {
                // Get the job which is in progress
                var jobInProgress = _pendingJobs[result.JobId];
                // Check if the clientid mismatches
                if (jobInProgress.AssignedCliendId != result.ClientId)
                {
                    Logger.Warn("Got job '{0}' result for differet client ('{1}' instead '{2}')", result.JobId, result.ClientId, jobInProgress.AssignedCliendId);
                    return false;
                }

                // Check if there was an error processing the job
                if (result.HasError)
                {
                    Logger.Error("Got failed result for job '{0}': {1}", result.JobId, result.Error.ToString());
                    Interlocked.Increment(ref _totalFailedJobs);
                    // If so, remove it from the in-progress list
                    _pendingJobs.Remove(result.JobId);
                    // Reset the assigned values
                    jobInProgress.Reset();
                    // Add the job to the queue again
                    _availableJobs.Enqueue(jobInProgress);
                    return false;
                }

                var resultString = result.GetOutput();
                Logger.Info("Got result for job '{0}': {1}", result.JobId, resultString);
                Interlocked.Increment(ref _totalProcessedJobs);

                // Remove job from in-progress list
                _pendingJobs.Remove(result.JobId);
                // Set the result values
                jobInProgress.ResultTime = DateTime.Now;
                jobInProgress.ResultString = resultString;
                // Add it to the finished queue
                _finishedJobs.Enqueue(jobInProgress);
                _resultAvailableWaitHandle.Set();
            }
            return true;
        }

        /// <summary>
        /// Enqueues the given job
        /// </summary>
        private void EnqueueJob(IJobInput jobInput, object additionalData = null)
        {
            var jobWrapper = new JobWrapper
            {
                Id = Guid.NewGuid(),
                HandlerId = Id,
                JobInput = jobInput,
                EnqueueTime = DateTime.Now,
                AdditionalData = additionalData
            };
            _availableJobs.Enqueue(jobWrapper);
        }

        /// <summary>
        /// Control thread for this handler which is run when it is started
        /// - Refills the job-queue if needed
        /// - Checks for job timeouts and then resends the jobs
        /// - Collects and processes the results
        /// </summary>
        private void ControlThread()
        {
            // Notify the handler that it has started
            _handler.OnStart();

            while (!_controlTaskCancelToken.IsCancellationRequested)
            {
                // Collect results
                JobWrapper finishedJob;
                while (_finishedJobs.TryDequeue(out finishedJob))
                {
                    Logger.Debug("Collecting finished job '{0}' with result '{1}'", finishedJob.Id, finishedJob.ResultString);
                    _handler.ProcessResult(finishedJob.JobInput, finishedJob.ResultString);
                }

                // Check for jobs with a timeout
                if (_handlerSettings.JobTimeout > 0)
                {
                    var now = DateTime.Now;
                    var jobsToRequeue = new List<JobWrapper>();
                    lock (_pendingJobs.GetSyncRoot())
                    {
                        foreach (var kvp in _pendingJobs)
                        {
                            if (now - kvp.Value.AssignedTime > TimeSpan.FromSeconds(_handlerSettings.JobTimeout))
                            {
                                // Job had a timeout
                                jobsToRequeue.Add(kvp.Value);
                            }
                        }
                        foreach (var job in jobsToRequeue)
                        {
                            Logger.Warn("Job '{0}' had a timeout", job.Id);
                            _pendingJobs.Remove(job.Id);
                            job.Reset();
                            _availableJobs.Enqueue(job);
                        }
                    }
                }

                // Refill available jobs if needed
                if (_availableJobs.IsEmpty)
                {
                    Logger.Debug("Job queue is empty, adding new jobs");
                    // Fill with jobs
                    _handler.CreateMoreJobs();
                    Logger.Debug("Job queue contains now {0} job(s)", _availableJobs.Count);
                }

                // Stop if the handler was marked as finished
                if (_handler.IsFinished)
                {
                    Logger.Info("Handler '{0}' finished successfully", Id);
                    lock (_lockObject)
                    {
                        _handler.OnFinished();
                        HandlerState = HandlerState.Finished;
                        _controlTask = null;
                        return;
                    }
                }

                // Sleep a little or until any of the various events was set
                WaitHandle.WaitAny(new[] { _controlTaskCancelToken.Token.WaitHandle, _jobsEmptyWaitHandle, _resultAvailableWaitHandle }, 5000);
            }
        }

        /// <summary>
        /// Register the log event to the given sink
        /// </summary>
        public void RegisterSink(EventSink<LogEventArgs> sink)
        {
            Logger.LogEvent += sink.CallbackMethod;
        }
    }
}
