﻿using System;
using System.Diagnostics;
using System.Timers;
using System.IO;
using System.Threading.Tasks;

namespace OpenXStreamLoader
{
    class Task : IDisposable
    {
        public enum TaskState
        {
            InProgress,
            Waiting,
            Finished,
            Stopped,
            StartProcessError
        };

        public interface IStatusView
        {
            TaskState State { get; }
            string FileName { get; }
            long FileSize { get; }
            DateTime Created { get; }
            DateTime Ended { get; }
            string ConsoleOutput { get; }
        };

        internal class Status : IStatusView
        {
            public TaskState State { get; set; } = TaskState.Stopped;
            public string FileName { get; set; }
            public long FileSize { get; set; } = 0;
            public DateTime Created { get; set; }
            public DateTime Ended { get; set; }
            public string ConsoleOutput { get; set; }
        }

        public IStatusView TaskStatus
        {
            get
            {
                return _status;
            }
        }

        public delegate void StatusChangedCallback(string url, IStatusView status);
        public delegate void CheckOnlineCallback(string url);
        public delegate string GetFinalFileNameFromTemplate(string fileNameTemplate);

        private readonly object _consoleLock = new object();

        private string _url;
        private string _quality;
        private bool _performOnlineCheck;
        private string _executablePath;
        private string _streamlinkOptions;
        private string _outputFileNameTemplate;
        private string _fileName;
        private Process _process;
        private System.Timers.Timer _onlineCheckTimer;
        private System.Timers.Timer _statusCheckTimer;
        private StatusChangedCallback _statusChanged;
        CheckOnlineCallback _checkOnline;
        private int _waitingTaskInterval;
        GetFinalFileNameFromTemplate _getFinalFileNameFromTemplate;
        private Status _status;
        private bool _processExiting;

        public Task(string url, string quality, bool performOnlineCheck, string executablePath, string streamlinkOptions, string outputFileNameTemplate, StatusChangedCallback statusChanged, CheckOnlineCallback checkOnline, GetFinalFileNameFromTemplate getFinalFileNameFromTemplate, int waitingTaskInterval)
        {
            _url = url;
            _quality = quality;
            _performOnlineCheck = performOnlineCheck;
            _executablePath = executablePath;
            _streamlinkOptions = streamlinkOptions;
            _outputFileNameTemplate = outputFileNameTemplate;
            _statusChanged = statusChanged;
            _checkOnline = checkOnline;
            _getFinalFileNameFromTemplate = getFinalFileNameFromTemplate;
            _waitingTaskInterval = waitingTaskInterval;

            _status = new Status();

            _onlineCheckTimer = new System.Timers.Timer();
            _onlineCheckTimer.Enabled = false;
            _onlineCheckTimer.Interval = _waitingTaskInterval * 1000; // ms
            _onlineCheckTimer.Elapsed += new ElapsedEventHandler(onOnlineCheckTimer);

            _statusCheckTimer = new System.Timers.Timer();
            _statusCheckTimer.Enabled = false;
            _statusCheckTimer.Interval = 3 * 1000; // ms
            _statusCheckTimer.Elapsed += new ElapsedEventHandler(onStatusCheckTimer);
        }

        ~Task()
        {
            Dispose();
        }

        public void Dispose()
        {
            _onlineCheckTimer.Enabled = false;
            _statusCheckTimer.Enabled = false;
            stopProcess();
        }

        public void Start(Boolean asWaiting = false)
        {
            start(asWaiting);
        }

        public void Stop()
        {
            _onlineCheckTimer.Enabled = false;
            _statusCheckTimer.Enabled = false;
            stopProcess();
            _status.State = TaskState.Stopped;
            reportStatus();
        }

        public void ResumeOnline()
        {
            if (_status.State == TaskState.Waiting || _status.State == TaskState.StartProcessError && _performOnlineCheck)
            {
                start();
            }
        }

        private void start(Boolean asWaiting = false)
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    return;
                }
            }
            catch
            {
                //todo
            }

            startProcess(asWaiting);
        }

        private void startProcess(Boolean asWaiting = false)
        {
            _process = new Process();
            _process.StartInfo.UseShellExecute = false;
            _process.StartInfo.RedirectStandardOutput = true;
            _process.StartInfo.FileName = _executablePath;
            _process.StartInfo.CreateNoWindow = true;
            _process.EnableRaisingEvents = true;
            _process.StartInfo.WorkingDirectory = "";
            _process.OutputDataReceived += new DataReceivedEventHandler(onOutputDataReceived);
            _process.Exited += new EventHandler(onProcessExited);
            _fileName = _getFinalFileNameFromTemplate(_outputFileNameTemplate);
            _status.State = TaskState.InProgress;
            _status.FileName = _fileName;
            _status.Created = DateTime.Now;
            _status.Ended = DateTime.Now;
            _status.FileSize = -1;
            _status.ConsoleOutput = "Started: " + DateTime.Now.ToString("dd-MM-yyyy HH꞉mm꞉ss") + "\n";
            _process.StartInfo.Arguments = " " + _streamlinkOptions + " -o \"" + _fileName + "\" -f " + _url + " " + _quality;

            try
            {
                _processExiting = false;

                if (asWaiting)
                {
                    _status.State = TaskState.Waiting;
                    _checkOnline(_url); //queue asap
                }
                else
                {
                    _process.Start();
                    _process.BeginOutputReadLine();
                }

                _onlineCheckTimer.Enabled = asWaiting && _performOnlineCheck;
                _statusCheckTimer.Enabled = !asWaiting;
            }
            catch (Exception exception)
            {
                _status.State = TaskState.StartProcessError;
                _status.ConsoleOutput += DateTime.Now.ToString("dd-MM-yyyy HH꞉mm꞉ss") + "\n" + exception.Message + "\n";
                _onlineCheckTimer.Enabled = _performOnlineCheck;
                _statusCheckTimer.Enabled = false;
            }

            reportStatus();
        }

        private void stopProcess()
        {
            _status.Ended = DateTime.Now;
            _status.ConsoleOutput += "Stopped " + DateTime.Now.ToString("dd-MM-yyyy HH꞉mm꞉ss") + "\n";
            _processExiting = true;

            try
            {
                if (_process == null || _process.HasExited)
                {
                    return;
                }
            }
            catch
            {
                return;
            }

            lock (_consoleLock)
            {
                try
                {
                    var id = _process.Id;

                    if (id == 0)
                    {
                        return;
                    }

                    // Attaching to the console and sending control break event didn't work (main application process shuts down itself occasionally):
                    //https://stackoverflow.com/questions/2055753/how-to-gracefully-terminate-a-process
                    //https://github.com/gapotchenko/Gapotchenko.FX/blob/1accd5c03a310a925939ee55a9bd3055dadb4baa/Source/Gapotchenko.FX.Diagnostics.Process/ProcessExtensions.End.cs#L247-L328
                    //
                    // So have to do it brutally by killing the Streamlink.exe process and its Python.exe child processes

                    Utils.killProcessTree(id);
                }
                catch
                {

                }
            }

            try
            {
                _process.Dispose();
            }
            catch
            {

            }

            _process = null;
        }

        private void onProcessExited(object sender, System.EventArgs e)
        {
            if (_processExiting)  //User stopped process or app shut down
            {
                return;
            }

            _statusCheckTimer.Enabled = false;
            _status.Ended = DateTime.Now;
            _status.ConsoleOutput += "Exited " + DateTime.Now.ToString("dd-MM-yyyy HH꞉mm꞉ss") + "\n";

            // Was current recording ended by time limit?  Make sure last file had duration before repeating.
            if (_performOnlineCheck && recordableStreamFound()  && _status.FileSize > 1000 )
            {
                startProcess();  //continue recording in new file
            }
            else if (playableStreamFound() && !streamSupportsQuality())
            {
                _status.State = TaskState.StartProcessError;
                _onlineCheckTimer.Enabled = _performOnlineCheck;
            }
            else if (_performOnlineCheck)
            {
                _onlineCheckTimer.Enabled = true;
                _status.State = TaskState.Waiting;
            }
            else
            {
                _status.State = TaskState.Finished;
            }

            reportStatus();
        }

        private void onOnlineCheckTimer(object source, ElapsedEventArgs e)
        {
            _checkOnline(_url);
        }

        private void onStatusCheckTimer(object source, ElapsedEventArgs e)
        {
            _status.FileSize = getFileSize();
            reportStatus();
        }

        private void reportStatus()
        {
            _statusChanged(_url, _status);
        }

        private void onOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            //todo lock
            _status.ConsoleOutput += e.Data + "\n";
        }

        private long getFileSize()
        {
            if (!File.Exists(_fileName))
            {
                return -1;
            }

            try
            {
                return new FileInfo(_fileName).Length;
            }
            catch
            {
                return -1;
            }
        }

        private bool recordableStreamFound()
        {
            return _status.ConsoleOutput.Contains("Stopping stream early after");
        }

        private bool playableStreamFound()
        {
            return !_status.ConsoleOutput.Contains("No playable streams found");
        }

        private bool streamSupportsQuality()
        {
            return !_status.ConsoleOutput.Contains("The specified stream(s) '" + _quality + "' could not be found.");
        }
    }
}
