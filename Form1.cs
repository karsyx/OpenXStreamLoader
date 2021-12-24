﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Text.RegularExpressions;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Threading;
using System.Xml;
using Newtonsoft.Json.Linq;

namespace OpenXStreamLoader
{
    public partial class Form1 : Form
    {
        private enum OnlineCheckPriority
        {
            Low,
            High
        }

        public enum OnlineStatus
        {
            Unknown,
            Offline,
            Public,
            Private,
            Hidden,
            Away,
            Error,
            Error429
        }

        internal class TaskConfiguration
        {
            public bool _waitForOnline;
            public string _quality;
            public string _fileNameTemplate;
        }

        internal class TaskData
        {
            public Task _task;
            public ListViewItem _listItem;
            public TaskConfiguration _config;
            public string _consoleOutput;
        };

        internal class FavoriteData
        {
            public ListViewItem _item;
            public OnlineStatus _status;
            public Image _profileImage;
        }

        private readonly float _version = 0.5f;
        private readonly int _trayBalloonTimeout = 5000; // ms
        private readonly string _streamlinkDefaultOptions = "--hls-timeout 120 --hls-playlist-reload-attempts 20 --hls-segment-timeout 90 --hds-segment-threads 8 --hls-segment-threads 8 --hds-timeout 120 --hds-segment-timeout 90 --hds-segment-attempts 20";
        private readonly object _onlineCheckQueueLock = new object();

        private readonly string _site1String1 = "aHR0cHM6Ly9yb29taW1nLnN0cmVhbS5oaWdod2VibWVkaWEuY29tL3JpLw==".from64();
        private readonly string _site1String2 = "aHR0cHM6Ly9jaGF0dXJiYXRlLmNvbS9nZXRfZWRnZV9obHNfdXJsX2FqYXgv".from64();

        private Settings _settings;
        private Dictionary<string, TaskData> _tasks;
        private LinkedList<string> _onlineCheckLowPriorityQueue;
        private LinkedList<string> _onlineCheckHighPriorityQueue;
        private bool _onlineCheckIsRunning;
        private Thread _onlineCheckThread;
        private Dictionary<string, FavoriteData> _favoritesMap;
        private Dispatcher _dispatcher;
        private CookieContainer _cookies;
        private PreviewForm _previewForm;
        private bool _showingProfilePictures = false;
        private bool _appClosing = false;
        private Bitmap _offlineImage;
        private RecordsColumnSorter _recordsColumnSorter;

        public Form1()
        {
            InitializeComponent();

            _recordsColumnSorter = new RecordsColumnSorter();
            lvTasks.ListViewItemSorter = _recordsColumnSorter;
            lvTasks.enableDoubleBuffering();
            lvFavorites.enableDoubleBuffering(true);
            _previewForm = new PreviewForm();

            _settings = new Settings();
            _tasks = new Dictionary<string, TaskData>();
            _onlineCheckLowPriorityQueue = new LinkedList<string>();
            _onlineCheckHighPriorityQueue = new LinkedList<string>();
            _favoritesMap = new Dictionary<string, FavoriteData>();
            _dispatcher = Dispatcher.CurrentDispatcher;
            _cookies = new CookieContainer();

            ServicePointManager.Expect100Continue = true;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            loadConfiguration();
            setVersion();
            tmrFavoritesStatusCheck.Interval = _settings._favoritesUpdateInterval * 1000;
            tmrFavoritesStatusCheck.Enabled = true;

            initOnlineCheck();

            _onlineCheckIsRunning = true;
            _onlineCheckThread = new Thread(new ThreadStart(onlineCheckProc));
            _onlineCheckThread.Start();

        }

        private void Form1_Load(object sender, EventArgs e)
        {
            if (_settings._recordOnStart)
            {
                startAllTasks();
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_settings._minimizeToTray)
            {
                if (!_appClosing)
                {
                    e.Cancel = true;
                    Hide();
                }
            }
            else
            {
                e.Cancel = MessageBox.Show("Close application?", "OpenXStreamLoader", MessageBoxButtons.YesNo) == DialogResult.No;
            }
        }

        private void Form1_FormClosed(object sender, FormClosedEventArgs e)
        {
            stopOnlineCheckThread();
            saveConfiguration();
        }

        private void setVersion()
        {
            var versionString = _version.ToString();

            Text += versionString;
            lbVersion.Text += versionString;
        }

        private void loadConfiguration()
        {
            setDefaultSettings();
            loadConfigurationFromXml();
            applySettings();
        }

        private void setDefaultSettings()
        {
            _settings._streamlinkExePath = "Streamlink_Portable\\Streamlink.exe";
            _settings._streamlinkOptions = _streamlinkDefaultOptions;
            _settings._defaultRecordsPath = "";
            _settings._browserPath = "";
            _settings._httpRequestDelay = 300;
            _settings._favoritesUpdateInterval = 60;
            _settings._waitingTaskInterval = 30;
            _settings._minimizeToTray = false;
            _settings._showOnlineNotification = true;
            _settings._recordOnStart = false;
        }

        private void applySettings()
        {
            tbStreamlinkExePath.Text = _settings._streamlinkExePath;
            tbStreamlinkOptions.Text = _settings._streamlinkOptions;
            tbDefaultRecordsPath.Text = _settings._defaultRecordsPath;
            tbBrowserPath.Text = _settings._browserPath;
            nuHttpRequestDelay.Value = _settings._httpRequestDelay;
            nuFavoritesUpdateInterval.Value = _settings._favoritesUpdateInterval;
            nuWaitingTaskInterval.Value = _settings._waitingTaskInterval;
            cbMinimizeToTray.Checked = _settings._minimizeToTray;
            cbOnlineNotification.Checked = _settings._showOnlineNotification;
            cbRecordOnStart.Checked = _settings._recordOnStart;

            trayIcon.Visible = _settings._minimizeToTray || _settings._showOnlineNotification;
            tmrFavoritesStatusCheck.Interval = _settings._favoritesUpdateInterval * 1000;
        }

        private void loadConfigurationFromXml()
        {
            XmlDocument doc = new XmlDocument();

            try
            {
                if (!File.Exists("config.xml"))
                {
                    return;
                }

                doc.Load("config.xml");

                if (doc.GetElementsByTagName("Configuration").Count == 0)
                {
                    return;
                }

                var configurationElement = doc.GetElementsByTagName("Configuration")[0];
                var settingsElement = configurationElement["Settings"];
                var recordsElement = configurationElement["Records"];
                var lastViewedElement = configurationElement["LastViewed"];
                var favoritesElement = configurationElement["Favorites"];

                var streamlinkExeElement = settingsElement["StreamlinkExe"];
                var streamlinkOptionsElement = settingsElement["StreamlinkOptions"];
                var defaultRecordsPathElement = settingsElement["DefaultRecordsPath"];
                var browserPathElement = settingsElement["BrowserPath"];

                if (streamlinkExeElement != null)
                {
                    _settings._streamlinkExePath = streamlinkExeElement.InnerText;
                }

                if (streamlinkOptionsElement != null)
                {
                    _settings._streamlinkOptions = streamlinkOptionsElement.InnerText;
                }

                if (defaultRecordsPathElement != null)
                {
                    _settings._defaultRecordsPath = defaultRecordsPathElement.InnerText;
                }

                if (browserPathElement != null)
                {
                    _settings._browserPath = browserPathElement.InnerText;
                }

                if (settingsElement.Attributes["HttpRequestDelay"] != null)
                {
                    _settings._httpRequestDelay = settingsElement.Attributes["HttpRequestDelay"].InnerText.ToInt32Def(500).Clamp(nuHttpRequestDelay.Minimum.ToInt32(), nuHttpRequestDelay.Maximum.ToInt32());
                }

                if (settingsElement.Attributes["FavoritesUpdateInterval"] != null)
                {
                    _settings._favoritesUpdateInterval = settingsElement.Attributes["FavoritesUpdateInterval"].InnerText.ToInt32Def(300).Clamp(nuFavoritesUpdateInterval.Minimum.ToInt32(), nuFavoritesUpdateInterval.Maximum.ToInt32());
                }

                if (settingsElement.Attributes["WaitingTaskInterval"] != null)
                {
                    _settings._waitingTaskInterval = settingsElement.Attributes["WaitingTaskInterval"].InnerText.ToInt32Def(60).Clamp(nuWaitingTaskInterval.Minimum.ToInt32(), nuWaitingTaskInterval.Maximum.ToInt32());
                }

                if (settingsElement.Attributes["Quality"] != null)
                {
                    cbQuality.Text = settingsElement.Attributes["Quality"].InnerText.Trim();
                }

                if (settingsElement.Attributes["MinimizeToTray"] != null)
                {
                    _settings._minimizeToTray = settingsElement.Attributes["MinimizeToTray"].InnerText.ToBoolean();
                }

                if (settingsElement.Attributes["ShowOnlineNotification"] != null)
                {
                    _settings._showOnlineNotification = settingsElement.Attributes["ShowOnlineNotification"].InnerText.ToBoolean();
                }

                if (settingsElement.Attributes["RecordOnStart"] != null)
                {
                    _settings._recordOnStart = settingsElement.Attributes["RecordOnStart"].InnerText.ToBoolean();
                }

                if (recordsElement != null)
                {
                    var recordElements = recordsElement.GetElementsByTagName("*");

                    for (int i = 0; i < recordElements.Count; i++)
                    {
                        var recordElement = recordElements[i];

                        if (recordElement.Attributes["Url"] != null)
                        {
                            string url = recordElement.Attributes["Url"].InnerText;
                            bool waitForOnline = true;
                            string quality = "best";
                            string fileNameTemplate = "";

                            if (recordElement.Attributes["WaitForOnline"] != null)
                            {
                                waitForOnline = recordElement.Attributes["WaitForOnline"].InnerText.ToBoolean();
                            }

                            if (recordElement.Attributes["Quality"] != null)
                            {
                                quality = recordElement.Attributes["Quality"].InnerText;
                            }

                            if (recordElement.Attributes["FileNameTemplate"] != null)
                            {
                                fileNameTemplate = recordElement.Attributes["FileNameTemplate"].InnerText;
                            }

                            addTask(url, quality, fileNameTemplate);
                        }
                    }
                }

                if (lastViewedElement != null)
                {
                    var lastViewedElements = lastViewedElement.GetElementsByTagName("*");

                    for (int i = 0; i < lastViewedElements.Count; i++)
                    {
                        if (lastViewedElements[i].Attributes["Url"] != null)
                        {
                            cbId.Items.Add(lastViewedElements[i].Attributes["Url"].InnerText);
                        }
                    }
                }

                if (favoritesElement != null)
                {
                    var favoriteElements = favoritesElement.GetElementsByTagName("*");

                    for (int i = 0; i < favoriteElements.Count; i++)
                    {
                        if (favoriteElements[i].Attributes["Url"] != null)
                        {
                            string url = favoriteElements[i].Attributes["Url"].InnerText;
                            Image profileImage = null;

                            if (favoriteElements[i].Attributes["ProfileImage"] != null)
                            {
                                try
                                {
                                    using (var stream = new MemoryStream(System.Convert.FromBase64String(favoriteElements[i].Attributes["ProfileImage"].InnerText)))
                                    {
                                        profileImage = Image.FromStream(stream);
                                    }
                                }
                                catch
                                {
                                    profileImage = null;
                                }
                            }

                            addToFavorites(url, profileImage);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show("Failed to load settings from \"config.xml\": " + exception.Message);
            }
        }

        private void saveConfiguration()
        {
            XmlDocument doc = new XmlDocument();
            XmlWriterSettings settings = new XmlWriterSettings();

            var configurationElement = doc.CreateElement("Configuration");
            var settingsElement = doc.CreateElement("Settings");
            var recordsElement = doc.CreateElement("Records");
            var lastViewedElement = doc.CreateElement("LastViewed");
            var favoritesElement = doc.CreateElement("Favorites");

            doc.AppendChild(configurationElement);
            configurationElement.AppendChild(settingsElement);
            configurationElement.AppendChild(recordsElement);
            configurationElement.AppendChild(lastViewedElement);
            configurationElement.AppendChild(favoritesElement);

            var streamlinkExeElement = doc.CreateElement("StreamlinkExe");
            var streamlinkOptionsElement = doc.CreateElement("StreamlinkOptions");
            var defaultRecordsPathElement = doc.CreateElement("DefaultRecordsPath");

            var browserPathElement = doc.CreateElement("BrowserPath");

            streamlinkExeElement.InnerText = _settings._streamlinkExePath;
            streamlinkOptionsElement.InnerText = _settings._streamlinkOptions;
            defaultRecordsPathElement.InnerText = _settings._defaultRecordsPath;
            browserPathElement.InnerText = _settings._browserPath;

            settingsElement.AppendChild(streamlinkExeElement);
            settingsElement.AppendChild(streamlinkOptionsElement);
            settingsElement.AppendChild(defaultRecordsPathElement);
            settingsElement.AppendChild(browserPathElement);

            settingsElement.SetAttribute("HttpRequestDelay", _settings._httpRequestDelay.ToString());
            settingsElement.SetAttribute("FavoritesUpdateInterval", _settings._favoritesUpdateInterval.ToString());
            settingsElement.SetAttribute("WaitingTaskInterval", _settings._waitingTaskInterval.ToString());
            settingsElement.SetAttribute("Quality", cbQuality.Text.Trim());
            settingsElement.SetAttribute("MinimizeToTray", _settings._minimizeToTray.ToString());
            settingsElement.SetAttribute("ShowOnlineNotification", _settings._showOnlineNotification.ToString());
            settingsElement.SetAttribute("RecordOnStart", _settings._recordOnStart.ToString());

            foreach (var taskP in _tasks)
            {
                try
                {
                    var element = doc.CreateElement("Item");

                    element.SetAttribute("Url", taskP.Key);
                    element.SetAttribute("WaitForOnline", taskP.Value._config._waitForOnline.ToString());
                    element.SetAttribute("Quality", taskP.Value._config._quality);
                    element.SetAttribute("FileNameTemplate", taskP.Value._config._fileNameTemplate);

                    recordsElement.AppendChild(element);
                }
                catch { }

            }

            for (int i = 0; i < cbId.Items.Count; i++)
            {
                try
                {

                    var url = cbId.Items[i].ToString();
                    var element = doc.CreateElement("Item");

                    element.SetAttribute("Url", url);
                    lastViewedElement.AppendChild(element);
                }
                catch { }
            }

            for (int i = 0; i < lvFavorites.Items.Count; i++)
            {
                try
                {
                    var url = lvFavorites.Items[i].Text;
                    var data = _favoritesMap[url];
                    var element = doc.CreateElement("Item");

                    element.SetAttribute("Url", url);

                    if (data._profileImage != null)
                    {
                        using (var imageStream = new MemoryStream())
                        using (var bmp = new Bitmap(data._profileImage))
                        {
                            bmp.Save(imageStream, System.Drawing.Imaging.ImageFormat.Jpeg);
                            element.SetAttribute("ProfileImage", System.Convert.ToBase64String(imageStream.ToArray()));
                        }
                    }

                    favoritesElement.AppendChild(element);
                }
                catch { }
            }

            settings.Indent = true;
            settings.IndentChars = "  ";
            settings.NewLineHandling = NewLineHandling.Replace;
            settings.Encoding = Encoding.UTF8;
            settings.NewLineChars = "\r\n";

            doc.Save(XmlWriter.Create("config.xml", settings));
        }

        private HttpWebRequest creatWebRequest(string url, int timeout = 5000 /*ms*/)
        {
            var request = WebRequest.Create(url) as HttpWebRequest;

            request.CookieContainer = _cookies;
            request.Method = "GET";
            request.KeepAlive = false;
            request.Timeout = timeout;

            return request;
        }

        private bool checkForNewVersion()
        {
            try
            {
                using (var response = creatWebRequest("http://github.com/voidtemp/OpenXStreamLoader/releases", 8000).GetResponse())
                {
                    StreamReader streamReader = new StreamReader(response.GetResponseStream());
                    string pageText = streamReader.ReadToEnd();
                    Regex regex = new Regex("<a href=\"\\/voidtemp\\/OpenXStreamLoader\\/tree\\/.*\\\" class=\\\"muted-link css-truncate\\\" title=\\\"(?<string>.*)\\\">");
                    string versionString = regex.Match(pageText).Groups["string"].ToString().ToLower();
                    float version = versionString.ToFloat32Def(0.0f);

                    if (version > _version)
                    {
                        lbVersionInfo.Text = "New version is available: v" + versionString;

                        if (MessageBox.Show("New version available: v" + versionString + "\nOpen github releases page?", "OpenXStreamLoader", MessageBoxButtons.YesNo) == DialogResult.Yes)
                        {
                            openUrlInBrowser("https://github.com/voidtemp/OpenXStreamLoader/releases");
                        }

                        return true;
                    }
                    else
                    {
                        lbVersionInfo.Text = "Current version is the latest.";
                    }
                }
            }
            catch
            {
                lbVersionInfo.Text = "Failed to retrieve latest version info.";
            }

            return false;
        }

        private void initOnlineCheck()
        {
            try
            {
                using (var response = (HttpWebResponse)creatWebRequest(_site1String1 + "_________.jpg").GetResponse())
                {
                    if (response.StatusCode == HttpStatusCode.OK && response.ContentType == "image/jpeg")
                    {
                        _offlineImage = new Bitmap(response.GetResponseStream());
                    }
                }
            }
            catch
            {

            }
        }

        private void btChooseStreamlinkExe_Click(object sender, EventArgs e)
        {
            if (openStreamlinkExeDialog.ShowDialog() == DialogResult.OK)
            {
                _settings._streamlinkExePath = openStreamlinkExeDialog.FileName;
                tbStreamlinkExePath.Text = openStreamlinkExeDialog.FileName;
            }
        }

        private void tbStreamlinkExePath_TextChanged(object sender, EventArgs e)
        {
            _settings._streamlinkExePath = tbStreamlinkExePath.Text.Trim();
        }

        private void btChooseDefaultRecordsPath_Click(object sender, EventArgs e)
        {
            using (var openFolderDialog = new CommonOpenFileDialog()
            {
                IsFolderPicker = true
            })
            {
                if (openFolderDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    _settings._defaultRecordsPath = openFolderDialog.FileName;
                    tbDefaultRecordsPath.Text = _settings._defaultRecordsPath;
                }
            }
        }

        private void btStartRecord_Click(object sender, EventArgs e)
        {
            string url = cbId.Text.Trim();
            string quality = cbQuality.Text.Trim();

            if (String.IsNullOrEmpty(url))
            {
                return;
            }

            if (!File.Exists(_settings._streamlinkExePath))
            {
                MessageBox.Show("Please provide path to Streamlink.exe");
                tabsControl.SelectTab(2);

                return;
            }

            if (_tasks.ContainsKey(url))
            {
                var taskData = _tasks[url];

                lvTasks.SelectedItems.Clear();
                taskData._listItem.Selected = true;
                taskData._task.Start();
            }
            else
            {
                var task = addTask(url, quality, getFinalFileNameTemplate());

                task.Start();
                addLastViewed(url);
            }
        }

        private Task addTask(string url, string quality, string fileNameTemplate)
        {
            if (_tasks.ContainsKey(url))
            {
                return null;
            }

            string fullPath = Utils.getFullPathWithEndingSlash(fileNameTemplate);

            if (!String.IsNullOrEmpty(fullPath))
            {
                Directory.CreateDirectory(fullPath);
            }

            Task task = new Task(url, quality, cbOnlineCheck.Checked, _settings._streamlinkExePath, _settings._streamlinkOptions, fileNameTemplate, onTaskStatusChangedEvent, checkTastUrlOnline, getFinalFileNameFromTemplate, _settings._waitingTaskInterval);
            ListViewItem listItem = new ListViewItem(url);

            listItem.Tag = task.TaskStatus;
            listItem.SubItems.Add(cbOnlineCheck.Checked ? "✓" : "");
            listItem.SubItems.Add("Stopped");
            listItem.SubItems.Add("");
            listItem.SubItems.Add(quality);
            listItem.SubItems.Add("");
            listItem.SubItems.Add("");
            listItem.SubItems.Add("");
            lvTasks.Items.Add(listItem);

            _tasks.Add(url, new TaskData()
            {
                _task = task,
                _listItem = listItem,
                _config = new TaskConfiguration()
                {
                    _waitForOnline = cbOnlineCheck.Checked,
                    _quality = quality,
                    _fileNameTemplate = fileNameTemplate
                }
            });

            return task;
        }

        private void cbId_SelectedIndexChanged(object sender, EventArgs e)
        {
            checkIdName();
            printFinalFileName();
        }

        private void cbId_TextChanged(object sender, EventArgs e)
        {
            checkIdName();
            printFinalFileName();
        }

        private void checkIdName()
        {
            if (cbSameNameAsId.Checked)
            {
                string id = Utils.getIdFromUrl(cbId.Text);

                if (!String.IsNullOrEmpty(id))
                {
                    tbFileName.Text = id;
                }
            }
        }

        private string getFinalFileNameTemplate()
        {
            string fileName = tbFileName.Text.Trim();
            string quality = cbQuality.Text.Trim();
            int qIndex = quality.IndexOf(',');//todo what is this for?
            if (qIndex > 0) quality = quality.Substring(0, qIndex - 1);

            if (!Path.IsPathRooted(fileName))
            {
                if (!String.IsNullOrEmpty(_settings._defaultRecordsPath))
                {
                    fileName = Utils.pathAddBackSlash(_settings._defaultRecordsPath) + fileName;
                }
            }

            string fullPath = Utils.getFullPathWithEndingSlash(fileName);
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);

            if (String.IsNullOrEmpty(extension))
            {
                extension = ".ts";
            }

            fileName = fullPath + fileNameWithoutExtension + " [%DATE%][" + quality + "].ts";
            //todo C# Sanitize File Name

            return fileName;
        }

        private string getFinalFileNameFromTemplate(string fileNameTemplate)
        {
            return Utils.getNonExistingFileName(fileNameTemplate.Replace("%DATE%", DateTime.Now.ToString("dd-MM-yyyy HH꞉mm꞉ss")));
        }

        private string getFinalFileName()
        {
            return getFinalFileNameFromTemplate(getFinalFileNameTemplate());
        }

        private void printFinalFileName()
        {
            tbFinalFileName.Text = getFinalFileName();
        }

        private void tbDefaultRecordsPath_TextChanged(object sender, EventArgs e)
        {
            _settings._defaultRecordsPath = tbDefaultRecordsPath.Text.Trim();
        }

        private void onTaskStatusChangedEvent(string url, Task.IStatusView status)
        {
            _dispatcher.Invoke(() =>
            {
                onTaskStatusChanged(url, status);
            });
        }

        private void updateTaskFileInfo(ListViewItem item, Task.IStatusView status, bool isRecording = false)
        {
            if (status.FileSize > 0)
            {
                item.SubItems[5].Text = isRecording? getDurationString(status.Created, DateTime.Now):
                                                     getDurationString(status.Created, status.Ended);
                item.SubItems[6].Text = Utils.formatBytes(status.FileSize);
                item.SubItems[7].Text = status.FileName;
            }
        }

        private void updateModelStatus(string url, OnlineStatus status) //todo Model class
        {
            if (!_tasks.ContainsKey(url))
            {
                return;
            }

            var task = _tasks[url];
            var item = task._listItem;

            lvTasks.BeginUpdate();
            item.SubItems[3].Text = status.ToString() + "  " + DateTime.Now.ToString("HH꞉mm");
            lvTasks.EndUpdate();
        }

        private void onTaskStatusChanged(string url, Task.IStatusView status)
        {
            if (!_tasks.ContainsKey(url))
            {
                return;
            }

            var task = _tasks[url];
            var item = task._listItem;

            task._consoleOutput = status.ConsoleOutput;
            lvTasks.BeginUpdate();

            switch (status.State)
            {
                case Task.TaskState.InProgress:
                {
                    item.SubItems[2].Text = "Recording...";
                    item.BackColor = Color.Lime;
                    updateTaskFileInfo(item, status, true);

                    break;
                }

                case Task.TaskState.Waiting:
                {
                    item.SubItems[2].Text = "Waiting...";
                    item.BackColor = Color.Gold;
                    updateTaskFileInfo(item, status);

                    break;
                }

                case Task.TaskState.Finished:
                {
                    item.SubItems[2].Text = "Finished";
                    item.BackColor = SystemColors.Window;
                    updateTaskFileInfo(item, status);

                    break;
                }

                case Task.TaskState.Stopped:
                {
                    item.SubItems[2].Text = "Stopped";
                    item.BackColor = SystemColors.Window;
                    updateTaskFileInfo(item, status);

                    break;
                }

                case Task.TaskState.StartProcessError:
                {
                    item.SubItems[2].Text = "Error";
                    item.BackColor = Color.Red;

                    break;
                }

                default:
                {
                    item.BackColor = SystemColors.Window;

                    break;
                }
            }

            lvTasks.EndUpdate();
        }

        private void addLastViewed(string url)
        {
            // put it on top
            if (cbId.Items.Contains(url))
            {
                cbId.Items.Remove(url);
            }

            cbId.Items.Insert(0, url);
        }

        private void tbFileName_TextChanged(object sender, EventArgs e)
        {
            printFinalFileName();
        }

        private void cbSameNameAsId_CheckedChanged(object sender, EventArgs e)
        {
            checkIdName();
        }

        private void btAddToFavorites_Click(object sender, EventArgs e)
        {
            addToFavorites(cbId.Text.Trim(), null, OnlineCheckPriority.High);
        }

        private void addToFavorites(string url, Image profileImage = null, OnlineCheckPriority priority = OnlineCheckPriority.Low)
        {
            if (String.IsNullOrEmpty(url) || hasFavorite(url))
            {
                return;
            }

            ListViewItem item = new ListViewItem(url);

            _favoritesMap.Add(url, new FavoriteData { _item = item, _status = OnlineStatus.Unknown, _profileImage = profileImage });
            item.ImageKey = "Checking";
            item.SubItems.Add("checking...");
            lvFavorites.Items.Add(item);

            if (profileImage != null)
            {
                updateImageList(ilProfilePictures, url, new Bitmap(profileImage, ilFavImages.ImageSize));
            }

            queueOnlineStatusCheck(url, priority);
        }

        private bool hasFavorite(string url)
        {
            return _favoritesMap.ContainsKey(url);
        }

        private void setFavoriteStatus(string url, OnlineStatus onlineStatus)
        {
            if (!_favoritesMap.ContainsKey(url))
            {
                return;
            }

            var data = _favoritesMap[url];
            var item = data._item;

            if (_settings._showOnlineNotification && data._status != OnlineStatus.Public && onlineStatus == OnlineStatus.Public)
            {
                trayIcon.Tag = url;
                trayIcon.ShowBalloonTip(_trayBalloonTimeout, "OpenXStreamLoader", "\"" + Utils.getIdFromUrl(url) + "\" is online now", ToolTipIcon.Info);
            }

            data._status = onlineStatus;
            lvFavorites.BeginUpdate();
            setItemStatusImage(item, onlineStatus);

            if (onlineStatus == OnlineStatus.Public)
            {
                updateFavoriteImage(url);
            }

            lvFavorites.EndUpdate();
        }

        private void updateFavoriteImage(string url)
        {
            if (!_favoritesMap.ContainsKey(url))
            {
                return;
            }

            var data = _favoritesMap[url];
            Regex regex = new Regex("\\.com\\/(?<string>(.*))\\/");
            string id = regex.Match(url).Groups["string"].ToString();

            try
            {
                using (var response = (HttpWebResponse)creatWebRequest(_site1String1 + id + ".jpg").GetResponse())
                {
                    if (response.StatusCode == HttpStatusCode.OK &&
                        response.ContentType == "image/jpeg")
                    {
                        var bitmap = new Bitmap(response.GetResponseStream());
                        var bitmapResized = new Bitmap(bitmap, ilFavImages.ImageSize);

                        updateImageList(ilFavImages, url, bitmapResized);
                        updateImageList(ilProfilePictures, url, bitmapResized);
                        data._profileImage = bitmap;
                        data._item.ImageKey = url;
                    }
                    else
                    {
                        data._item.ImageKey = "Error";
                    }
                }
            }
            catch
            {
                data._item.ImageKey = "Error";
            }
        }

        private void updateFavoritesStatus()
        {
            for (int i = 0; i < lvFavorites.Items.Count; i++)
            {
                queueOnlineStatusCheck(lvFavorites.Items[i].Text, OnlineCheckPriority.Low);
            }
        }

        private void setAllChecking()
        {
            lvFavorites.BeginUpdate();

            for (int i = 0; i < lvFavorites.Items.Count; i++)
            {
                var item = lvFavorites.Items[i];
                var url = item.Text;

                item.ImageKey = "Checking";
                item.SubItems[1].Text = "checking...";
                item.BackColor = SystemColors.Window;
            }

            lvFavorites.EndUpdate();
        }

        private void tmrFavoritesStatusCheck_Tick(object sender, EventArgs e)
        {
            updateFavoritesStatus();
        }

        private void checkTastUrlOnline(string url)
        {
            queueOnlineStatusCheck(url, OnlineCheckPriority.High);
        }

        private void queueOnlineStatusCheck(string url, OnlineCheckPriority priority)
        {
            lock (_onlineCheckQueueLock)
            {
                if (_onlineCheckHighPriorityQueue.Contains(url))
                {
                    return;
                }

                if (priority == OnlineCheckPriority.High)
                {
                    if (_onlineCheckLowPriorityQueue.Contains(url))
                    {
                        _onlineCheckLowPriorityQueue.Remove(url);
                    }

                    _onlineCheckHighPriorityQueue.AddLast(url);
                }
                else if (priority == OnlineCheckPriority.Low)
                {
                    if (_onlineCheckLowPriorityQueue.Contains(url))
                    {
                        return;
                    }

                    _onlineCheckLowPriorityQueue.AddLast(url);
                }

                Monitor.Pulse(_onlineCheckQueueLock);
            }
        }

        private void stopOnlineCheckThread()
        {
            _onlineCheckIsRunning = false;

            lock (_onlineCheckQueueLock)
            {
                Monitor.Pulse(_onlineCheckQueueLock);
            }

            _onlineCheckThread.Join();
        }

        private void onlineCheckProc()
        {
            int _httpRequestDelay = _settings._httpRequestDelay;
            string url;

            while (_onlineCheckIsRunning)
            {
                for (int high = 0; (getQueueCount(_onlineCheckHighPriorityQueue) > 0 || getQueueCount(_onlineCheckLowPriorityQueue) > 0) && _onlineCheckIsRunning;)
                {
                    _httpRequestDelay = _settings._httpRequestDelay;

                    if (high < 2)
                    {
                        if (getUrlFromQueue(_onlineCheckHighPriorityQueue, out url))
                        {
                            dispatchOnlineCheckResult(url, requestUrlOnlineStatus(url));
                            System.Threading.Thread.Sleep(_httpRequestDelay);
                            high++;

                            continue;
                        }
                    }

                    high = 0;

                    if (getUrlFromQueue(_onlineCheckLowPriorityQueue, out url))
                    {
                        dispatchOnlineCheckResult(url, requestUrlOnlineStatus(url));
                        System.Threading.Thread.Sleep(_httpRequestDelay);
                    }
                }

                lock (_onlineCheckQueueLock)
                {
                    while (_onlineCheckHighPriorityQueue.Count == 0 && _onlineCheckLowPriorityQueue.Count == 0 && _onlineCheckIsRunning)
                    {
                        Monitor.Wait(_onlineCheckQueueLock);
                    }
                }
            }
        }

        private bool getUrlFromQueue(LinkedList<string> queue, out string url)
        {
            lock (_onlineCheckQueueLock)
            {
                if (queue.Count > 0)
                {
                    url = queue.First.Value;
                    queue.RemoveFirst();

                    return true;
                }
            }

            url = "";

            return false;
        }

        private int getQueueCount(LinkedList<string> queue)
        {
            lock (_onlineCheckQueueLock)
            {
                return queue.Count;
            }
        }

        private void dispatchOnlineCheckResult(string url, OnlineStatus status)
        {
            _dispatcher.InvokeAsync(() =>
            {
                onOnlineCheckResult(url, status);
            });
        }

        private void onOnlineCheckResult(string url, OnlineStatus status)
        {
            setFavoriteStatus(url, status);
            updateTask(url, status);
        }

        private void updateTask(string url, OnlineStatus status)
        {
            if (!_tasks.ContainsKey(url))
            {
                return;
            }

            var task = _tasks[url];

            updateModelStatus(url, status);

            if (status == OnlineStatus.Public)
            {
                _tasks[url]._task.ResumeOnline();
            }
        }

        private OnlineStatus requestUrlOnlineStatus(string url)
        {
            return site1OnlineStatusCheckMethod2(url);
        }

        private OnlineStatus site1OnlineStatusCheckMethod1(string url)
        {
            OnlineStatus result = OnlineStatus.Unknown;

            try
            {
                string id = Utils.getIdFromUrl(url);
                var request = WebRequest.Create(_site1String2) as HttpWebRequest;
                var postData = "room_slug=" + Uri.EscapeDataString(id) + "&bandwidth=" + Uri.EscapeDataString("high");
                var data = Encoding.ASCII.GetBytes(postData);

                request.CookieContainer = _cookies;
                request.Method = "POST";
                request.ContentType = "application/x-www-form-urlencoded";
                request.ContentLength = data.Length;
                request.Referer = url;
                request.Headers.Add("X-Requested-With", "XMLHttpRequest");

                using (var stream = request.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        return OnlineStatus.Error;
                    }

                    StreamReader streamReader = new StreamReader(response.GetResponseStream());
                    string responseString = streamReader.ReadToEnd();
                    string status = JObject.Parse(responseString)["room_status"].ToString();

                    if (status == "public")
                    {
                        result = OnlineStatus.Public;
                    }
                    else if (status == "private")
                    {
                        result = OnlineStatus.Private;
                    }
                    else if (status == "hidden")
                    {
                        result = OnlineStatus.Hidden;
                    }
                    else if (status == "away")
                    {
                        result = OnlineStatus.Away;
                    }
                    else if (status == "offline")
                    {
                        result = OnlineStatus.Offline;
                    }
                }
            }
            catch (Exception exception)
            {
                return OnlineStatus.Error;
            }

            return result;
        }

        private OnlineStatus site1OnlineStatusCheckMethod2(string url)
        {
            string id = Utils.getIdFromUrl(url);

            try
            {
                using (var response = (HttpWebResponse)creatWebRequest(_site1String1 + id + ".jpg").GetResponse())
                {
                    if (response.StatusCode == HttpStatusCode.OK &&
                        response.ContentType == "image/jpeg")
                    {
                        if (!Utils.isBitmapsEqual(_offlineImage, new Bitmap(response.GetResponseStream())))
                        {
                            return site1OnlineStatusCheckMethod1(url);
                        }
                        else
                        {
                            return OnlineStatus.Offline; //todo unknown
                        }
                    }
                    else
                    {
                        return OnlineStatus.Error;
                    }
                }
            }
            catch
            {
                return OnlineStatus.Error;
            }
        }

        private void updateNowToolStripMenuItem_Click(object sender, EventArgs e)
        {
            setAllChecking();
            updateFavoritesStatus();
        }

        private void btChooseBrowserPath_Click(object sender, EventArgs e)
        {
            using (var openFolderDialog = new CommonOpenFileDialog()
            {
                IsFolderPicker = true
            })
            {
                if (openFolderDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    _settings._browserPath = openFolderDialog.FileName;
                    tbBrowserPath.Text = _settings._browserPath;
                }
            }
        }

        private void tbBrowserPath_TextChanged(object sender, EventArgs e)
        {
            _settings._browserPath = tbBrowserPath.Text.Trim();
        }

        private void openInBrowserToolStripMenuItem_Click(object sender, EventArgs e)
        {
            openFavoriteInBrowser();
        }

        private void lvFavorites_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            openFavoriteInBrowser();
        }

        private void openFavoriteInBrowser()
        {
            if (lvFavorites.SelectedItems.Count == 0)
            {
                return;
            }

            openUrlInBrowser(lvFavorites.SelectedItems[0].Text);
        }

        private void openUrlInBrowser(string url)
        {
            try
            {
                if (String.IsNullOrEmpty(_settings._browserPath))
                {
                    System.Diagnostics.Process.Start(url);
                }
                else
                {
                    if (!Utils.runCmd(_settings._browserPath + " " + url))
                    {
                        MessageBox.Show("Failed to open browser");
                    }
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show("Failed to open browser: " + exception.Message);
            }
        }

        private void cmFavorites_Opening(object sender, CancelEventArgs e)
        {
            bool isItemClicked = lvFavorites.SelectedItems.Count > 0;

            startRecordToolStripMenuItem.Enabled = isItemClicked;
            openInBrowserToolStripMenuItem.Enabled = isItemClicked;
            copyURLToClipboardToolStripMenuItem.Enabled = isItemClicked;
            showImageorHoverWithCtrlPressedToolStripMenuItem.Enabled = isItemClicked;
            deleteFavToolStripMenuItem.Enabled = isItemClicked;
            updateThisToolStripMenuItem.Enabled = isItemClicked;
        }

        private void startRecordToolStripMenuItem_Click(object sender, EventArgs e)
        {
            openFavoriteForRecord();
        }

        private void openFavoriteForRecord()
        {
            if (lvFavorites.SelectedItems.Count == 0)
            {
                return;
            }

            cbId.Text = lvFavorites.SelectedItems[0].Text;
            tabsControl.SelectTab(0);
            btStartRecord_Click(null, null);
        }

        private void showImageorHoverWithCtrlPressedToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (lvFavorites.SelectedItems.Count == 0)
            {
                return;
            }

            var screen = Screen.FromControl(this).Bounds;

            _previewForm.setImage(_favoritesMap[lvFavorites.SelectedItems[0].Text]._profileImage);
            _previewForm.Location = new Point((screen.Width - MousePosition.X > _previewForm.Width) ? MousePosition.X : screen.Width - _previewForm.Width,
                (screen.Height - MousePosition.Y > _previewForm.Height) ? MousePosition.Y : screen.Height - _previewForm.Height);
            _previewForm.Activate();
            _previewForm.Show();
        }

        private void copyURLToClipboardToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (lvFavorites.SelectedItems.Count == 0)
            {
                return;
            }

            Clipboard.SetText(lvFavorites.SelectedItems[0].Text);
        }

        private void deleteFavToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (lvFavorites.SelectedItems.Count == 0)
            {
                return;
            }

            string url = lvFavorites.SelectedItems[0].Text;

            if (MessageBox.Show("Delete \"" + url + "\"?", "OpenXStreamLoader", MessageBoxButtons.YesNo) == DialogResult.No)
            {
                return;
            }

            if (_favoritesMap.ContainsKey(url))
            {
                lvFavorites.Items.Remove(_favoritesMap[url]._item);
                _favoritesMap.Remove(url);
            }
        }

        private void updateThisToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (lvFavorites.SelectedItems.Count == 0)
            {
                return;
            }

            var item = lvFavorites.SelectedItems[0];
            string url = item.Text;

            if (_favoritesMap.ContainsKey(url))
            {
                item.ImageKey = "Checking";
                item.SubItems[1].Text = "checking...";
                item.BackColor = SystemColors.Window;
                queueOnlineStatusCheck(url, OnlineCheckPriority.High);
            }
        }

        private static string getDurationString(DateTime start, DateTime end)
        {
            return end.Subtract(start).ToString(@"hh\:mm\:ss");
        }

        private void viewStreamLinkOutputToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (lvTasks.SelectedItems.Count == 0)
            {
                return;
            }

            var url = lvTasks.SelectedItems[0].Text;

            if (_tasks.ContainsKey(url))
            {
                MessageBox.Show(_tasks[url]._consoleOutput);
            }
        }

        private void cmTasks_Opening(object sender, CancelEventArgs e)
        {
            bool isSingle = lvTasks.SelectedItems.Count == 1;
            bool isMulti = lvTasks.SelectedItems.Count >= 1;

            openFileToolStripMenuItem.Enabled = isSingle;
            openTaskUrlInBrowserToolStripMenuItem.Enabled = isMulti;
            showInFileExplorerToolStripMenuItem.Enabled = isSingle;
            addTaskToFavoritesToolStripMenuItem.Enabled = isMulti;
            copyURLToInputFieldToolStripMenuItem.Enabled = isSingle;
            startToolStripMenuItem.Enabled = isMulti;
            stopToolStripMenuItem.Enabled = isMulti;
            deleteToolStripMenuItem.Enabled = isMulti;
            viewStreamLinkOutputToolStripMenuItem.Enabled = isSingle;
        }

        private void openTaskUrlInBrowserToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (ListViewItem item in lvTasks.SelectedItems)
            {
                openUrlInBrowser(item.Text);
            }
        }

        private void showInFileExplorerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (lvTasks.SelectedItems.Count != 1)
            {
                return;
            }

            var filename = lvTasks.SelectedItems[0].SubItems[7].Text;

            if (String.IsNullOrEmpty(filename))
            {
                return;
            }

            try
            {
                System.Diagnostics.Process.Start("explorer.exe", "/select, \"" + filename + "\"");
            }
            catch (Exception exception)
            {
                MessageBox.Show("Failed to open file \"" + filename + "\":\n" + exception.Message);
            }
        }

        private void addTaskToFavoritesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (ListViewItem item in lvTasks.SelectedItems)
            {
                addToFavorites(item.Text);
            }
        }

        private void copyURLToInputFieldToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (lvTasks.SelectedItems.Count != 1)
            {
                return;
            }

            Clipboard.SetText(lvTasks.SelectedItems[0].Text);
        }

        private void startToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (ListViewItem item in lvTasks.SelectedItems)
            {
                string url = item.Text;

                if (_tasks.ContainsKey(url))
                {
                    _tasks[url]._task.Start();
                }
            }
        }

        private void startAllToolStripMenuItem_Click(object sender, EventArgs e)
        {
            startAllTasks();
        }

        private void stopToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (ListViewItem item in lvTasks.SelectedItems)
            {
                string url = item.Text;

                if (_tasks.ContainsKey(url))
                {
                    _tasks[url]._task.Stop();
                }
            }
        }

        private void deleteToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Delete selected tasks?", "OpenXStreamLoader", MessageBoxButtons.YesNo) == DialogResult.No)
            {
                return;
            }

            foreach (ListViewItem item in lvTasks.SelectedItems)
            {
                string url = item.Text;

                if (_tasks.ContainsKey(url))
                {
                    _tasks[url]._task.Dispose();
                    _tasks.Remove(url);
                }
            }

            foreach (ListViewItem item in lvTasks.SelectedItems)
            {
                lvTasks.Items.Remove(item);
            }
        }

        private void deleteAllToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Delete all tasks?", "OpenXStreamLoader", MessageBoxButtons.YesNo) == DialogResult.No)
            {
                return;
            }

            foreach (var task in _tasks)
            {
                task.Value._task.Dispose();
                lvTasks.Items.Remove(task.Value._listItem);
            }

            _tasks.Clear();
        }

        private void openFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            openTaskFile();
        }

        private void lvTasks_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            openTaskFile();
        }

        private void openTaskFile()
        {
            if (lvTasks.SelectedItems.Count != 1)
            {
                return;
            }

            var filename = lvTasks.SelectedItems[0].SubItems[7].Text;

            if (String.IsNullOrEmpty(filename))
            {
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(filename);
            }
            catch (Exception exception)
            {
                MessageBox.Show("Failed to open file \"" + filename + "\":\n" + exception.Message);
            }
        }

        private void nuFavoritesUpdateInterval_ValueChanged(object sender, EventArgs e)
        {
            _settings._favoritesUpdateInterval = nuFavoritesUpdateInterval.Value.ToInt32();
            tmrFavoritesStatusCheck.Interval = _settings._favoritesUpdateInterval * 1000;
        }

        private void nuWaitingTaskInterval_ValueChanged(object sender, EventArgs e)
        {
            _settings._waitingTaskInterval = nuWaitingTaskInterval.Value.ToInt32();
        }

        private void nuHttpRequestDelay_ValueChanged(object sender, EventArgs e)
        {
            _settings._httpRequestDelay = nuHttpRequestDelay.Value.ToInt32();
        }

        private void tabPage2_Enter(object sender, EventArgs e)
        {
            ActiveControl = lvFavorites;
        }

        private void btStreamlinkDefaultOptions_Click(object sender, EventArgs e)
        {
            _settings._streamlinkOptions = _streamlinkDefaultOptions;
            tbStreamlinkOptions.Text = _settings._streamlinkOptions;
        }

        private void tbStreamlinkOptions_TextChanged(object sender, EventArgs e)
        {
            _settings._streamlinkOptions = tbStreamlinkOptions.Text;
        }

        private void tmrCheckForNewVersion_Tick(object sender, EventArgs e)
        {
            tmrCheckForNewVersion.Enabled = false;
            checkForNewVersion();
        }

        private void lbProductPage_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            openUrlInBrowser(lbProductPage.Text);
        }

        private void lbReleases_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            openUrlInBrowser(lbReleases.Text);
        }

        private void lbStreamlinkOnlineHelp_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            openUrlInBrowser("https://streamlink.github.io/cli.html");
        }

        private void lvFavorites_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control)
            {
                _showingProfilePictures = true;
                showFavoriteProfilePictures();
            }
        }

        private void lvFavorites_KeyUp(object sender, KeyEventArgs e)
        {
            if (!e.Control && _showingProfilePictures)
            {
                _showingProfilePictures = false;
                showFavoriteStatusPictures();
            }
        }

        private void showFavoriteProfilePictures()
        {
            lvFavorites.BeginUpdate();
            lvFavorites.LargeImageList = ilProfilePictures;

            foreach (ListViewItem item in lvFavorites.Items)
            {
                if (ilProfilePictures.Images.ContainsKey(item.Text))
                {
                    item.ImageKey = item.Text;
                }
                else
                {
                    item.ImageKey = "NoImage";
                }
            }

            lvFavorites.EndUpdate();
        }

        private void showFavoriteStatusPictures()
        {
            lvFavorites.BeginUpdate();
            lvFavorites.LargeImageList = ilFavImages;

            foreach (ListViewItem item in lvFavorites.Items)
            {
                if (_favoritesMap.ContainsKey(item.Text))
                {
                    setItemStatusImage(item, _favoritesMap[item.Text]._status);
                }
            }

            lvFavorites.EndUpdate();
        }

        private void setItemStatusImage(ListViewItem item, OnlineStatus status)
        {
            switch (status)
            {
                case OnlineStatus.Public:
                {
                    item.SubItems[1].Text = "Public";
                    item.ImageKey = item.Text;
                    item.BackColor = Color.Lime;

                    break;
                }

                case OnlineStatus.Private:
                {
                    item.SubItems[1].Text = "Private";
                    item.ImageKey = "Private";
                    item.BackColor = Color.Gold;

                    break;
                }

                case OnlineStatus.Hidden:
                {
                    item.SubItems[1].Text = "Hidden";
                    item.ImageKey = "Hidden";
                    item.BackColor = Color.Gold;

                    break;
                }

                case OnlineStatus.Away:
                {
                    item.SubItems[1].Text = "Away";
                    item.ImageKey = "Away";
                    item.BackColor = SystemColors.Window;

                    break;
                }

                case OnlineStatus.Error:
                {
                    item.SubItems[1].Text = "Error";
                    item.ImageKey = "Error";
                    item.BackColor = Color.Red;

                    break;
                }

                case OnlineStatus.Unknown:
                    {
                        item.SubItems[1].Text = "Unknown";
                        item.ImageKey = "Unknown";
                        item.BackColor = Color.Red;

                        break;
                    }

                default:
                {
                    item.SubItems[1].Text = "Offline";
                    item.ImageKey = "Offline";
                    item.BackColor = SystemColors.Window;

                    break;
                }
            }
        }

        private void updateImageList(ImageList imageList, string key, Image image)
        {
            if (imageList.Images.ContainsKey(key))
            {
                imageList.Images[imageList.Images.IndexOfKey(key)] = image;
            }
            else
            {
                imageList.Images.Add(key, image);
            }
        }

        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Show();
            Activate();
        }

        private void trayIcon_DoubleClick(object sender, EventArgs e)
        {
            Show();
            Activate();
        }

        private void quitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            _appClosing = true;
            Close();
        }

        private void cbMinimizeToTray_CheckedChanged(object sender, EventArgs e)
        {
            _settings._minimizeToTray = cbMinimizeToTray.Checked;
            trayIcon.Visible = _settings._minimizeToTray || _settings._showOnlineNotification;
        }

        private void cbOnlineNotification_CheckedChanged(object sender, EventArgs e)
        {
            _settings._showOnlineNotification = cbOnlineNotification.Checked;
            trayIcon.Visible = _settings._minimizeToTray || _settings._showOnlineNotification;
        }

        private void cbRecordOnStart_CheckedChanged(object sender, EventArgs e)
        {
            _settings._recordOnStart = cbRecordOnStart.Checked;
        }

        private void trayIcon_BalloonTipClicked(object sender, EventArgs e)
        {
            Show();
            Activate();
            tabsControl.SelectTab(1);

            try
            {
                string url = (sender as NotifyIcon).Tag as string;

                if (_favoritesMap.ContainsKey(url))
                {
                    var data = _favoritesMap[url];

                    data._item.Selected = true;
                    lvFavorites.EnsureVisible(lvFavorites.Items.IndexOf(data._item));
                }
            }
            catch
            {

            }
        }

        private void startAllTasks()
        {
            foreach (var task in _tasks)
            {
                task.Value._task.Start(asWaiting: true);
            }
        }

        private void lvTasks_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void lvTasks_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            // Determine if clicked column is already the column that is being sorted.
            if (e.Column == _recordsColumnSorter.SortColumn)
            {
                // Reverse the current sort direction for this column.
                if (_recordsColumnSorter.Order == SortOrder.Ascending)
                {
                    _recordsColumnSorter.Order = SortOrder.Descending;
                }
                else
                {
                    _recordsColumnSorter.Order = SortOrder.Ascending;
                }
            }
            else
            {
                // Set the column number that is to be sorted; default to ascending.
                _recordsColumnSorter.SortColumn = e.Column;
                _recordsColumnSorter.Order = SortOrder.Ascending;
            }

            // Perform the sort with these new sort options.
            lvTasks.Sort();
        }
    }
}
