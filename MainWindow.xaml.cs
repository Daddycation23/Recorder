using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.CoreAudioApi.Interfaces;
using TagLib;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Drawing; // For Icon
using System.Windows.Interop; // For Imaging

namespace Recorder;

public class AudioSessionInfo
{
    public int ProcessId { get; set; }
    public string? ProcessName { get; set; }
    public string DisplayName => $"{ProcessName ?? "Unknown"} (PID: {ProcessId})";
    public ImageSource? IconSource { get; set; } // Property to hold the icon
}

// Class to hold information about a finished recording
public class RecordingInfo : INotifyPropertyChanged
{
    private string _filePath = string.Empty;
    public string FilePath
    {
        get => _filePath;
        set
        {
            if (_filePath != value)
            {
                _filePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FileName));
            }
        }
    }

    public string FileName => System.IO.Path.GetFileName(FilePath);

    // Added metadata properties
    private string _artistTag = string.Empty;
    public string ArtistTag
    {
        get => _artistTag;
        set
        {
            if (_artistTag != value)
            {
                _artistTag = value;
                OnPropertyChanged();
            }
        }
    }

    private string _albumTag = string.Empty;
    public string AlbumTag
    {
        get => _albumTag;
        set
        {
            if (_albumTag != value)
            {
                _albumTag = value;
                OnPropertyChanged();
            }
        }
    }

    private TimeSpan _duration = TimeSpan.Zero;
    public TimeSpan Duration
    {
        get => _duration;
        set
        {
            if (_duration != value)
            {
                _duration = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DurationString));
            }
        }
    }

    public string DurationString => Duration.TotalSeconds < 1 ? "--:--" : 
                                  $"{(int)Duration.TotalMinutes:00}:{Duration.Seconds:00}";

    private DateTime _dateAdded = DateTime.Now;
    public DateTime DateAdded
    {
        get => _dateAdded;
        set
        {
            if (_dateAdded != value)
            {
                _dateAdded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DateAddedString));
            }
        }
    }

    public string DateAddedString => DateAdded.ToString("MM/dd/yyyy HH:mm");

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public string RecordingFormat { get; set; } = "MP3"; // Store the format used
}

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    // --- History File ---
    private const string HistoryFileName = "recording_history.json";
    private static readonly string HistoryFilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioRecorderApp", // App-specific folder
        HistoryFileName);
    // --------------------

    private WasapiLoopbackCapture? _capture;
    private LameMP3FileWriter? _mp3Writer;
    private WaveFileWriter? _wavWriter;
    private string _outputFilePath = string.Empty;
    private bool _isRecording = false;
    private Dictionary<int, bool>? _mutedSessions;

    private DispatcherTimer? _playbackMonitorTimer;
    private AudioSessionState _lastMonitoredSessionState = AudioSessionState.AudioSessionStateExpired;
    private const double MONITOR_INTERVAL_SECONDS = 1.0;

    // --- Property for Visualizer Binding ---
    private double _currentPeakLevel;
    public double CurrentPeakLevel
    {
        get => _currentPeakLevel;
        set
        {
            double clampedValue = Math.Max(0, Math.Min(100, value));
            if (_currentPeakLevel != clampedValue)
            {
                _currentPeakLevel = clampedValue;
                OnPropertyChanged(); 
            }
        }
    }
    // -------------------------------------

    public ObservableCollection<RecordingInfo> FinishedRecordings { get; } = new ObservableCollection<RecordingInfo>();
    // --- Collection for Saved Recordings (History) ---
    public ObservableCollection<RecordingInfo> SavedRecordings { get; } = new ObservableCollection<RecordingInfo>();
    // -------------------------------------------------

    private string _recordingFormatForCurrentFile = "MP3"; // Store the intended final format

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            
            this.Loaded += (s, e) => 
            {
                try 
                {
                    // Move initialization to the Loaded event
                    this.Closing += MainWindow_Closing;
                    this.DataContext = this;
                    
                    // Call these methods after the window is loaded
                    RefreshAudioApplications();
                    SetupPlaybackMonitorTimer();
                    LoadHistory();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error during window initialization: {ex.Message}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error creating window: {ex.Message}", "Window Creation Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetupPlaybackMonitorTimer()
    {
        _playbackMonitorTimer = new DispatcherTimer();
        _playbackMonitorTimer.Interval = TimeSpan.FromSeconds(MONITOR_INTERVAL_SECONDS);
        _playbackMonitorTimer.Tick += PollSessionState_Tick;
    }

    private void RefreshAudioApplications()
    {
        _playbackMonitorTimer?.Stop();

        if (AppComboBox != null)
        {
            AppComboBox.Items.Clear();
            _mutedSessions = new Dictionary<int, bool>();

            List<AudioSessionInfo> sessions = new List<AudioSessionInfo>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (device == null) 
                {
                    Debug.WriteLine("Error: Default audio endpoint not found.");
                    return;
                }

                var sessionManager = device.AudioSessionManager;
                if (sessionManager?.Sessions == null) 
                {
                    Debug.WriteLine("Error: Could not get AudioSessionManager or Sessions collection.");
                    return;
                }

                for (int i = 0; i < sessionManager.Sessions.Count; i++)
                {
                    AudioSessionControl? session = null;
                    try
                    {
                        session = sessionManager.Sessions[i];
                        if (session == null) continue;

                        int processId = 0;
                        AudioSessionState sessionState = AudioSessionState.AudioSessionStateExpired;
                        bool sessionInfoError = false;

                        // --- Try getting Process ID --- 
                        try
                        {
                            processId = (int)session.GetProcessID;
                            if (processId == 0) continue; // Skip system sounds process
                        }
                        catch (Exception pidEx)
                        {
                             Debug.WriteLine($"Error getting ProcessID for session index {i}: {pidEx.Message}");
                             sessionInfoError = true;
                        }
                        if (sessionInfoError) continue;
                        // -----------------------------

                        // --- Try getting Session State --- 
                        try 
                        {
                             sessionState = session.State; 
                        }
                        catch (Exception stateEx)
                        {
                            Debug.WriteLine($"Error getting State for session index {i} (PID: {processId}): {stateEx.Message}");
                            sessionInfoError = true;
                        }
                        if (sessionInfoError) continue;
                        // ------------------------------

                        // Wrap process access in its own try-catch
                        Process? process = null;
                        try
                        {
                            process = Process.GetProcessById(processId); // Can throw if process exited
                            if (process == null || string.IsNullOrWhiteSpace(process.ProcessName))
                            {
                                 process?.Dispose();
                                 continue;
                            }
                            
                            // Check session state (already retrieved)
                            if (sessionState == AudioSessionState.AudioSessionStateActive || sessionState == AudioSessionState.AudioSessionStateInactive)
                            {
                                 if (!sessions.Any(s => s.ProcessId == processId))
                                 {
                                    var sessionInfo = new AudioSessionInfo
                                    {
                                        ProcessId = processId,
                                        ProcessName = process.ProcessName,
                                        IconSource = GetIconForProcess(processId)
                                    };
                                    sessions.Add(sessionInfo);
                                 }
                            }
                        }
                        catch (ArgumentException argEx) // Process likely exited
                        {
                             Debug.WriteLine($"ArgumentException getting process {processId} (likely exited): {argEx.Message}");
                        }
                        catch (InvalidOperationException invEx) // Process likely exited or no access
                        {
                            Debug.WriteLine($"InvalidOperationException getting process {processId} (likely exited/no access): {invEx.Message}");
                        }
                        finally
                        {
                            process?.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        // Catch errors getting session info or PID
                        Debug.WriteLine($"Error processing session index {i}: {ex.Message}");
                    }
                    finally
                    {
                        session?.Dispose(); // Dispose session control COM object
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error enumerating audio sessions: {ex.Message}");
            }

            sessions = sessions.OrderBy(s => s.ProcessName).ToList();

            foreach(var sessionInfo in sessions)
            {
                 AppComboBox.Items.Add(sessionInfo);
            }

            if (AppComboBox.Items.Count > 0)
            {
                 AppComboBox.SelectedIndex = 0;
            }
            else
            {
                if (AutoStartCheckBox != null)
                {
                    AutoStartCheckBox.IsChecked = false;
                    AutoStartCheckBox.IsEnabled = false;
                }
                if (StartButton != null)
                {
                    StartButton.IsEnabled = false;
                }
            }

            Debug.WriteLine($"Found {AppComboBox.Items.Count} applications with audio sessions.");

            if (AutoStartCheckBox?.IsChecked == true && AppComboBox.SelectedItem != null)
            {
                _lastMonitoredSessionState = GetCurrentSessionState(((AudioSessionInfo)AppComboBox.SelectedItem).ProcessId);
                _playbackMonitorTimer?.Start();
            }
        }
    }

    private void RefreshAppsButton_Click(object sender, RoutedEventArgs e)
    {
         RefreshAudioApplications();
    }

    private void StartRecording()
    {
        AudioSessionInfo? selectedSessionInfo = AppComboBox.SelectedItem as AudioSessionInfo;
        if (selectedSessionInfo == null)
        {
            MessageBox.Show("Please select an application to record from the list.", "No Application Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_isRecording) return;

        // --- Get Selected Format --- 
        string selectedFormat = "MP3"; // Default
        if (FormatComboBox.SelectedItem is ComboBoxItem selectedItem)
        {
            selectedFormat = selectedItem.Content.ToString() ?? "MP3";
        }
        // --- Updated Extension Logic ---
        string fileExtension = ".mp3"; // Default
        if (selectedFormat.Equals("WAV", StringComparison.OrdinalIgnoreCase))
        {
            fileExtension = ".wav";
        }
        else if (selectedFormat.Equals("FLAC", StringComparison.OrdinalIgnoreCase))
        {
            fileExtension = ".flac";
        }
        // -----------------------------
        string tempFileName = $"recording_{selectedSessionInfo.ProcessName}_{DateTime.Now:yyyyMMdd_HHmmss}{fileExtension}";
        _outputFilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), tempFileName);

        Debug.WriteLine($"Temporary output file ({selectedFormat}): {_outputFilePath}");
        Debug.WriteLine($"Target process ID: {selectedSessionInfo.ProcessId}");

        _playbackMonitorTimer?.Stop();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessionManager = device?.AudioSessionManager;
            if (sessionManager?.Sessions == null) throw new InvalidOperationException("Could not get audio sessions.");

            _mutedSessions = new Dictionary<int, bool>();

            for (int i = 0; i < sessionManager.Sessions.Count; i++)
            {
                using var session = sessionManager.Sessions[i];
                if (session == null) continue;

                var processId = (int)session.GetProcessID;

                if (processId != 0 && processId != selectedSessionInfo.ProcessId && session.SimpleAudioVolume != null)
                {
                    try
                    {
                        bool originalMute = session.SimpleAudioVolume.Mute;
                        if (_mutedSessions.TryAdd(processId, originalMute))
                        {
                            session.SimpleAudioVolume.Mute = true;
                            Debug.WriteLine($"Muted session PID: {processId}");
                        }
                        else
                        {
                            Debug.WriteLine($"Process PID {processId} already muted (multiple sessions), skipping duplicate mute.");
                        }
                    }
                    catch(Exception muteEx) { Debug.WriteLine($"Failed to mute PID {processId}: {muteEx.Message}"); }
                }
            }
             Debug.WriteLine($"Muted {_mutedSessions.Count} other sessions.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error muting other applications: {ex.Message}\nRecording will capture all audio.");
             _mutedSessions?.Clear();
        }

        try
        {
            _capture = new WasapiLoopbackCapture();

            // Store the INTENDED format for potential post-processing
            _recordingFormatForCurrentFile = selectedFormat;

            // --- Create appropriate writer ---
            _mp3Writer = null; // Reset writers
            _wavWriter = null;

            var sourceFormat = _capture.WaveFormat;

            // If FLAC is selected, we initially write a WAV file
            if (selectedFormat.Equals("WAV", StringComparison.OrdinalIgnoreCase) || selectedFormat.Equals("FLAC", StringComparison.OrdinalIgnoreCase))
            {
                _wavWriter = new WaveFileWriter(_outputFilePath, sourceFormat);
                Debug.WriteLine($"Using WaveFileWriter for {selectedFormat} format (will convert FLAC later if needed).");
            }
            else // Default to MP3
            {
                _mp3Writer = new LameMP3FileWriter(_outputFilePath, sourceFormat, 192);
                Debug.WriteLine("Using LameMP3FileWriter for MP3 format.");
            }
            // ---------------------------------

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            _capture.StartRecording();
            _isRecording = true;
            Debug.WriteLine($"Recording started for PID {selectedSessionInfo.ProcessId} ({selectedFormat}) to temp file.");
            UpdateUI();
        }
        catch (DllNotFoundException dllEx) when (selectedFormat.Equals("MP3", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show($"Error starting MP3 recording: lame_enc.dll not found.\nDetails: {dllEx.Message}", "MP3 Encoder Error", MessageBoxButton.OK, MessageBoxImage.Error);
            CleanupRecording();
            UnmuteOtherApplications();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error starting recording: {ex.Message}");
            CleanupRecording();
            UnmuteOtherApplications();
        }
    }

    private void StopRecording()
    {
        if (!_isRecording || _capture == null) return;

        Debug.WriteLine("Attempting to stop recording...");
        _capture.StopRecording();
    }

    private void PollSessionState_Tick(object? sender, EventArgs e)
    {
        if (_isRecording || AppComboBox.SelectedItem == null)
        {
            return;
        }

        AudioSessionInfo selectedSessionInfo = (AudioSessionInfo)AppComboBox.SelectedItem;
        AudioSessionState currentState = GetCurrentSessionState(selectedSessionInfo.ProcessId);

        if ((_lastMonitoredSessionState == AudioSessionState.AudioSessionStateInactive || _lastMonitoredSessionState == AudioSessionState.AudioSessionStateExpired) &&
            currentState == AudioSessionState.AudioSessionStateActive)
        {
            Debug.WriteLine($"Detected active sound from PID {selectedSessionInfo.ProcessId}. Starting recording automatically.");
            StartRecording();
        }

        _lastMonitoredSessionState = currentState;
    }

    private AudioSessionState GetCurrentSessionState(int targetProcessId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (device == null) return AudioSessionState.AudioSessionStateExpired;

            var sessionManager = device.AudioSessionManager;
            if (sessionManager?.Sessions == null) return AudioSessionState.AudioSessionStateExpired;

            for (int i = 0; i < sessionManager.Sessions.Count; i++)
            {
                using var session = sessionManager.Sessions[i];
                if (session == null) continue;

                try
                {
                    if (session.GetProcessID == targetProcessId)
                    {
                        return session.State;
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"Error checking session PID {session.GetProcessID}: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting session state for PID {targetProcessId}: {ex.Message}");
        }

        return AudioSessionState.AudioSessionStateExpired;
    }

    private void AutoStartCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (_isRecording || AppComboBox.SelectedItem == null)
        {
            if (AppComboBox.SelectedItem == null) AutoStartCheckBox.IsChecked = false;
            return;
        }
        Debug.WriteLine("Auto-start monitoring enabled.");
        PollSessionState_Tick(null, EventArgs.Empty);
        _playbackMonitorTimer?.Start();
    }

    private void AutoStartCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("Auto-start monitoring disabled.");
        _playbackMonitorTimer?.Stop();
        _lastMonitoredSessionState = AudioSessionState.AudioSessionStateExpired;
    }

    private void AppComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _playbackMonitorTimer?.Stop();
        _lastMonitoredSessionState = AudioSessionState.AudioSessionStateExpired;

        if (AutoStartCheckBox.IsChecked == true && AppComboBox.SelectedItem != null && !_isRecording)
        {
             Debug.WriteLine($"Monitoring new selection: {((AudioSessionInfo)AppComboBox.SelectedItem).DisplayName}");
             PollSessionState_Tick(null, EventArgs.Empty);
             _playbackMonitorTimer?.Start();
        }

        bool appSelected = AppComboBox.SelectedItem != null;
        StartButton.IsEnabled = appSelected && !_isRecording;
        AutoStartCheckBox.IsEnabled = appSelected && !_isRecording;
        if (!appSelected) AutoStartCheckBox.IsChecked = false;
    }

    private void UnmuteOtherApplications()
    {
        if (_mutedSessions == null || _mutedSessions.Count == 0) return;

        Debug.WriteLine($"Unmuting {_mutedSessions.Count} sessions...");
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessionManager = device?.AudioSessionManager;
            if (sessionManager?.Sessions == null) return;

            for (int i = 0; i < sessionManager.Sessions.Count; i++)
            {
                using var session = sessionManager.Sessions[i];
                if (session == null) continue;

                var processId = (int)session.GetProcessID;

                if (_mutedSessions.TryGetValue(processId, out bool originalMuteState))
                {
                     if(session.SimpleAudioVolume != null)
                     {
                        try
                        {
                            session.SimpleAudioVolume.Mute = originalMuteState;
                            Debug.WriteLine($"Unmuted PID: {processId} (Restored to: {originalMuteState})");
                        }
                        catch(Exception unmuteEx) { Debug.WriteLine($"Failed to unmute PID {processId}: {unmuteEx.Message}");}
                     }
                }
            }
        }
        catch (Exception ex)
        {
             Debug.WriteLine($"Error during unmuting: {ex.Message}");
             MessageBox.Show($"Failed to unmute some applications: {ex.Message}");
        }
        finally
        {
            _mutedSessions.Clear();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded > 0)
        {
            // --- Write to the active writer ---
            if (_mp3Writer != null)
            {
                _mp3Writer.Write(e.Buffer, 0, e.BytesRecorded);
            }
            else if (_wavWriter != null)
            {
                _wavWriter.Write(e.Buffer, 0, e.BytesRecorded);
            }
            // ----------------------------------

            // --- Calculate Peak Level ---
            float maxSample = 0;
            for (int index = 0; index < e.BytesRecorded; index += 4)
            {
                if (index + 4 > e.BytesRecorded) break;
                float sample = BitConverter.ToSingle(e.Buffer, index);
                float absSample = Math.Abs(sample);
                if (absSample > maxSample)
                {
                    maxSample = absSample;
                }
            }
            double peakLevel = maxSample * 100.0;

            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                CurrentPeakLevel = peakLevel;
            }));
            // --------------------------
        }
        // Optional: Reset peak level if no bytes recorded
        // else 
        // {
        //     Application.Current.Dispatcher.BeginInvoke(new Action(() => CurrentPeakLevel = 0));
        // }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        Debug.WriteLine("Recording stopped.");
        string tempFilePath = _outputFilePath;
        UnmuteOtherApplications();
        CleanupRecording();
        _isRecording = false;

        Application.Current.Dispatcher.Invoke(() => {
             if (AutoStartCheckBox.IsChecked == true && AppComboBox.SelectedItem != null)
             {
                 PollSessionState_Tick(null, EventArgs.Empty);
                 _playbackMonitorTimer?.Start();
             }
             UpdateUI();
        });

        Application.Current.Dispatcher.Invoke(() =>
        {
            if (e.Exception != null)
            {
                Debug.WriteLine($"Recording stopped with error: {e.Exception.Message}");
                MessageBox.Show($"Recording stopped due to an error: {e.Exception.Message}");
                
                if (!string.IsNullOrEmpty(tempFilePath) && System.IO.File.Exists(tempFilePath))
                {
                    try { System.IO.File.Delete(tempFilePath); }
                    catch (Exception ex) { Debug.WriteLine($"Failed to delete temp file {tempFilePath} after error: {ex.Message}"); }
                }
            }
            else
            {
                Debug.WriteLine("Recording stopped successfully (temp file created).");
                if (System.IO.File.Exists(tempFilePath))
                {
                    string formatUsed = _recordingFormatForCurrentFile; // Use the intended final format
                    var newRecording = new RecordingInfo { FilePath = tempFilePath, RecordingFormat = formatUsed };

                    // --- Add post-processing step for FLAC ---
                    if (formatUsed.Equals("FLAC", StringComparison.OrdinalIgnoreCase))
                    {
                        // We need to run this asynchronously so it doesn't block the UI thread
                        // after recording stops. The item will be added to the list immediately,
                        // but the conversion happens in the background.
                         Task.Run(() => ConvertToFlacAsync(newRecording));
                         // The RecordingInfo added to the list will initially have the .wav path
                         // ConvertToFlacAsync will update it if conversion succeeds.
                    }
                    // -----------------------------------------

                    FinishedRecordings.Add(newRecording);
                }
                else
                {
                     Debug.WriteLine($"Temporary recording file not found at: {tempFilePath}");
                     MessageBox.Show($"Temporary recording file could not be created or found at: {tempFilePath}", "File Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        });
    }

    private void CleanupRecording()
    {
        _capture?.Dispose();
        _capture = null;

        // --- Dispose the active writer ---
        if (_mp3Writer != null)
        {
            _mp3Writer.Dispose();
            _mp3Writer = null;
        }
        if (_wavWriter != null)
        {
            _wavWriter.Dispose();
            _wavWriter = null;
        }
        // -------------------------------

        Debug.WriteLine("Recording resources cleaned up.");
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveHistory();

        _playbackMonitorTimer?.Stop();
        _playbackMonitorTimer = null;

        if (_isRecording)
        {
            StopRecording();
        }
        else
        {
            UnmuteOtherApplications();
            CleanupRecording();
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecording)
        {
            StopRecording();
        }
        else if (AppComboBox?.SelectedItem is AudioSessionInfo selectedSession)
        {
            StartRecording();
        }
        else
        {
            MessageBox.Show("Please select an application to record.", "No Application Selected", 
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
    }

    private void RecordingsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only enable/disable the Save button
        SaveButton.IsEnabled = RecordingsListView.SelectedItem != null;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Make sure a recording is selected
        if (RecordingsListView.SelectedItem is not RecordingInfo selectedRecording) return;

        string recordingFormat = GetSelectedFormat();
        string formatExtension = recordingFormat.ToLower();
        if (formatExtension == "mp3 automatic" || formatExtension == "mp3 320kbps") formatExtension = "mp3";

        SaveFileDialog saveFileDialog = new SaveFileDialog
        {
            Filter = $"{recordingFormat} Files|*.{formatExtension}|All Files|*.*",
            DefaultExt = formatExtension,
            FileName = System.IO.Path.GetFileNameWithoutExtension(selectedRecording.FileName) // Use existing filename without extension
        };

        bool? dialogResult = saveFileDialog.ShowDialog();
        if (dialogResult != true) return; // User cancelled

        string targetFilePath = saveFileDialog.FileName;

        try
        {
            // Copy the file
            bool success = true;
            string sourceFile = selectedRecording.FilePath;

            if (System.IO.File.Exists(sourceFile))
            {
                // Determine if format conversion is needed
                string sourceExt = System.IO.Path.GetExtension(sourceFile).ToLowerInvariant();
                string targetExt = System.IO.Path.GetExtension(targetFilePath).ToLowerInvariant();

                // Check if merge is requested
                if (MergeCheckBox.IsChecked == true && RecordingsListView.Items.Count > 1)
                {
                    MessageBox.Show("Merging files functionality will be implemented in a future update.", 
                                    "Feature Not Available", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Get metadata - use existing if available, otherwise default to empty
                string title = selectedRecording.ArtistTag ?? string.Empty;
                string artist = selectedRecording.ArtistTag ?? string.Empty; 
                string album = selectedRecording.AlbumTag ?? string.Empty;

                // These will still be available for backward compatibility
                if (string.IsNullOrEmpty(title) && TitleTextBox != null) title = TitleTextBox.Text;
                if (string.IsNullOrEmpty(artist) && ArtistTextBox != null) artist = ArtistTextBox.Text;
                if (string.IsNullOrEmpty(album) && AlbumTextBox != null) album = AlbumTextBox.Text;

                // Handle MP3 format with specific bitrate
                if (recordingFormat.Contains("320kbps") && sourceExt != targetExt)
                {
                    // Implement conversion to MP3 with 320kbps bitrate
                    using (var reader = new AudioFileReader(sourceFile))
                    using (var writer = new LameMP3FileWriter(targetFilePath, reader.WaveFormat, 320))
                    {
                        reader.CopyTo(writer);
                    }
                }
                // Format conversion needed
                else if (sourceExt != targetExt)
                {
                    if (targetExt == ".flac")
                    {
                        // For FLAC, we need a special conversion
                        ConvertToFlacAsync(selectedRecording).GetAwaiter().GetResult();
                        success = true; // Assume success if no exception thrown
                    }
                    else
                    {
                        // Default conversion using NAudio
                        using (var reader = new AudioFileReader(sourceFile))
                        {
                            if (targetExt == ".mp3")
                            {
                                using var writer = new LameMP3FileWriter(targetFilePath, reader.WaveFormat, 192); // Default to 192kbps
                                reader.CopyTo(writer);
                            }
                            else if (targetExt == ".wav")
                            {
                                using var writer = new WaveFileWriter(targetFilePath, reader.WaveFormat);
                                reader.CopyTo(writer);
                            }
                        }
                    }
                }
                else
                {
                    // Just copy the file if no conversion needed
                    System.IO.File.Copy(sourceFile, targetFilePath, true);
                }

                if (success)
                {
                    // Set ID3 tags if MP3, or other metadata for WAV/FLAC
                    try
                    {
                        using (var tagFile = TagLib.File.Create(targetFilePath))
                        {
                            tagFile.Tag.Title = title;
                            tagFile.Tag.Performers = new[] { artist };
                            tagFile.Tag.Album = album;
                            tagFile.Save();
                        }
                    }
                    catch (Exception tagEx)
                    {
                        Debug.WriteLine($"Failed to write tags: {tagEx.Message}");
                    }

                    // Create a record for the history
                    RecordingInfo savedRecording = new RecordingInfo
                    {
                        FilePath = targetFilePath,
                        RecordingFormat = recordingFormat
                    };

                    // Add to saved recordings and update history file
                    SavedRecordings.Add(savedRecording);
                    SaveHistory();

                    MessageBox.Show("File saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    
                    // Clear any input fields
                    if (TitleTextBox != null) TitleTextBox.Text = string.Empty;
                    if (ArtistTextBox != null) ArtistTextBox.Text = string.Empty;
                    if (AlbumTextBox != null) AlbumTextBox.Text = string.Empty;
                }
                else
                {
                    MessageBox.Show("There was an error saving the file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show("Source file not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Error Saving File", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Helper method to get the selected format 
    private string GetSelectedFormat()
    {
        if (FormatComboBox.SelectedItem is ComboBoxItem selectedItem)
        {
            return selectedItem.Content.ToString() ?? "MP3 Automatic";
        }
        return "MP3 Automatic"; // Default
    }

    private void UpdateUI()
    {
        bool controlsEnabled = !_isRecording;
        
        // Update controls state based on recording state
        if (StartButton != null)
        {
            StartButton.Content = _isRecording ? "Stop" : "Start";
            StartButton.IsEnabled = (AppComboBox?.SelectedItem != null) || _isRecording;
        }
        
        // Hide Stop button in the new UI as Start/Stop has been combined
        if (StopButton != null)
        {
            StopButton.IsEnabled = _isRecording;
        }
        
        // App selection should be disabled during recording
        if (AppComboBox != null)
        {
            AppComboBox.IsEnabled = controlsEnabled;
        }
        
        // Update hidden controls for backward compatibility
        if (RefreshAppsButton != null)
        {
            RefreshAppsButton.IsEnabled = controlsEnabled;
        }
        
        if (AutoStartCheckBox != null)
        {
            AutoStartCheckBox.IsEnabled = controlsEnabled && (AppComboBox?.SelectedItem != null);
            // Ensure AutoStart is unchecked if no app is selected
            if (AppComboBox?.SelectedItem == null && AutoStartCheckBox.IsChecked == true)
            {
                AutoStartCheckBox.IsChecked = false;
            }
        }
        
        // In the new UI, the Format ComboBox should be disabled during recording
        if (FormatComboBox != null)
        {
            FormatComboBox.IsEnabled = controlsEnabled;
        }
        
        // MergeCheckbox should be disabled during recording
        if (MergeCheckBox != null)
        {
            MergeCheckBox.IsEnabled = controlsEnabled;
        }
        
        // Save button should be enabled only when a recording is selected and not recording
        if (SaveButton != null) 
        {
            SaveButton.IsEnabled = controlsEnabled && RecordingsListView.SelectedItem != null;
        }
    }

    // --- Navigation Tab Handlers ---
    private void CaptureTabButton_Checked(object sender, RoutedEventArgs e)
    {
        if (CapturingViewArea != null && HistoryViewArea != null)
        {
            CapturingViewArea.Visibility = Visibility.Visible;
            HistoryViewArea.Visibility = Visibility.Collapsed;
        }
    }

    private void HistoryTabButton_Checked(object sender, RoutedEventArgs e)
    {
         if (CapturingViewArea != null && HistoryViewArea != null)
         {
            CapturingViewArea.Visibility = Visibility.Collapsed;
            HistoryViewArea.Visibility = Visibility.Visible;
         }
    }
    // -----------------------------

    // --- History Persistence Methods ---
    private void LoadHistory()
    {
        Debug.WriteLine($"Attempting to load history from: {HistoryFilePath}");
        try
        {
            // Ensure directory exists before trying to load
            string? directoryPath = System.IO.Path.GetDirectoryName(HistoryFilePath);
            if (directoryPath == null) return;

            if (!System.IO.Directory.Exists(directoryPath))
            {
                // Directory doesn't exist, so no history to load.
                 Debug.WriteLine("History directory does not exist. Skipping load.");
                 return;
            }

            if (System.IO.File.Exists(HistoryFilePath))
            {
                string json = System.IO.File.ReadAllText(HistoryFilePath);
                var loadedRecordings = JsonSerializer.Deserialize<List<RecordingInfo>>(json);

                if (loadedRecordings != null)
                {
                    SavedRecordings.Clear(); // Clear existing before loading
                    // --- Filter out entries where the file no longer exists --- 
                    foreach (var recording in loadedRecordings)
                    {
                        if (System.IO.File.Exists(recording.FilePath))
                        {
                            SavedRecordings.Add(recording);
                        }
                        else
                        {
                            Debug.WriteLine($"History item file not found, removing from history: {recording.FilePath}");
                        }
                    }
                    Debug.WriteLine($"Successfully loaded {SavedRecordings.Count} history items.");
                }
            }
            else
            {
                Debug.WriteLine("History file does not exist. No history loaded.");
            }
        }
        catch (System.IO.FileNotFoundException)
        {
            Debug.WriteLine("History file not found (FileNotFoundException). No history loaded.");
        }
        catch (JsonException ex)
        {
             Debug.WriteLine($"Error deserializing history file: {ex.Message}");
             MessageBox.Show($"Could not load recording history. The history file might be corrupt.\n{ex.Message}", "History Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
             Debug.WriteLine($"An unexpected error occurred loading history: {ex.Message}");
             MessageBox.Show($"An unexpected error occurred while loading recording history.\n{ex.Message}", "History Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveHistory()
    {
        Debug.WriteLine($"Attempting to save {SavedRecordings.Count} history items to: {HistoryFilePath}");
        try
        {
             // Ensure the directory exists
             string? directoryPath = System.IO.Path.GetDirectoryName(HistoryFilePath);
             if(directoryPath != null)
             {
                 System.IO.Directory.CreateDirectory(directoryPath); // Creates the directory if it doesn't exist

                 // Convert ObservableCollection to List for serialization
                 List<RecordingInfo> listToSave = new List<RecordingInfo>(SavedRecordings);

                 string json = JsonSerializer.Serialize(listToSave, new JsonSerializerOptions { WriteIndented = true });
                 System.IO.File.WriteAllText(HistoryFilePath, json);
                 Debug.WriteLine("History saved successfully.");
             }
             else
             {
                Debug.WriteLine("Could not determine directory path for history file. History not saved.");
             }
        }
        catch (Exception ex)
        {
             Debug.WriteLine($"An unexpected error occurred saving history: {ex.Message}");
             // Maybe inform the user, but avoid blocking exit if possible
             // MessageBox.Show($"Could not save recording history.\n{ex.Message}", "History Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    // ---------------------------------

    // --- History View Button Handlers ---
    private void HistoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool itemSelected = HistoryListView.SelectedItem != null;
        PlayHistoryButton.IsEnabled = itemSelected;
        OpenFolderButton.IsEnabled = itemSelected;
        DeleteHistoryButton.IsEnabled = itemSelected;
    }

    private void PlayHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryListView.SelectedItem is RecordingInfo selectedRecording)
        {
            if (System.IO.File.Exists(selectedRecording.FilePath))
            {
                try
                {
                    // Use Process.Start to open the file with the default associated application
                    Process.Start(new ProcessStartInfo(selectedRecording.FilePath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error trying to play file {selectedRecording.FilePath}: {ex.Message}");
                    MessageBox.Show($"Could not open the file:\n{ex.Message}", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show("The audio file for this entry could not be found.", "File Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                // Optionally remove the item from history here if the file is missing
                // SavedRecordings.Remove(selectedRecording);
                // SaveHistory(); // If removing, save the change
            }
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryListView.SelectedItem is RecordingInfo selectedRecording)
        {
            if (System.IO.File.Exists(selectedRecording.FilePath))
            {
                try
                {
                    string? directoryPath = System.IO.Path.GetDirectoryName(selectedRecording.FilePath);
                    if (!string.IsNullOrEmpty(directoryPath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = directoryPath,
                            UseShellExecute = true,
                            Verb = "open"
                        });
                    }
                    else
                    {
                         MessageBox.Show("Could not determine the folder path for this file.", "Error Opening Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                     Debug.WriteLine($"Error trying to open folder for {selectedRecording.FilePath}: {ex.Message}");
                     MessageBox.Show($"Could not open the folder:\n{ex.Message}", "Error Opening Folder", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
             else
            {
                MessageBox.Show("The audio file (and its folder) for this entry could not be found.", "File Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void DeleteHistoryButton_Click(object sender, RoutedEventArgs e)
    {
         if (HistoryListView.SelectedItem is RecordingInfo selectedRecording)
         {
            var result = MessageBox.Show($"Are you sure you want to delete this history entry?\n\n{selectedRecording.FileName}\n\nThis will remove the entry from the list. Do you also want to delete the audio file from your disk?",
                                         "Confirm Deletion", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel)
            {
                return; // User cancelled
            }

            bool deleteFile = (result == MessageBoxResult.Yes);

            // 1. Remove from collection
            SavedRecordings.Remove(selectedRecording);
            SaveHistory(); // Save the updated history list

            // 2. Optionally delete the file
            if (deleteFile)
            {
                if (System.IO.File.Exists(selectedRecording.FilePath))
                {
                    try
                    {
                        System.IO.File.Delete(selectedRecording.FilePath);
                        Debug.WriteLine($"Deleted audio file: {selectedRecording.FilePath}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error deleting file {selectedRecording.FilePath}: {ex.Message}");
                        MessageBox.Show($"The history entry was removed, but the audio file could not be deleted:\n{ex.Message}", "File Deletion Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else
                {
                     Debug.WriteLine($"File not found for deletion: {selectedRecording.FilePath}");
                     MessageBox.Show("The history entry was removed, but the audio file was already missing.", "File Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
         }
    }
    // ------------------------------------

    // --- INotifyPropertyChanged Implementation ---
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    // -----------------------------------------

    // --- Add FLAC Conversion Method ---
    private async Task ConvertToFlacAsync(RecordingInfo recordingToConvert)
    {
        string originalWavPath = recordingToConvert.FilePath;
        // Create the target .flac path by changing the extension
        string flacPath = System.IO.Path.ChangeExtension(originalWavPath, ".flac"); 

        Debug.WriteLine($"Attempting FLAC conversion: {originalWavPath} -> {flacPath}");

        // Basic check if ffmpeg might exist (improve this as needed)
        string ffmpegPath = "ffmpeg"; // Assumes ffmpeg is in PATH

        try
        {
            // TODO: Check if ffmpeg exists before trying to run?
            // Example: Run "ffmpeg -version" first?

            ProcessStartInfo processInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-i \"{originalWavPath}\" -c:a flac \"{flacPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using (Process process = Process.Start(processInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg process."))
            {
                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0 && System.IO.File.Exists(flacPath))
                {
                    Debug.WriteLine($"FFmpeg FLAC conversion successful. Output:\n{output}");
                    // Update the RecordingInfo FilePath
                    Application.Current.Dispatcher.Invoke(() => {
                         recordingToConvert.FilePath = flacPath;
                         // Now delete the original WAV file
                         try { System.IO.File.Delete(originalWavPath); } catch (Exception ex) { Debug.WriteLine($"Failed to delete temp WAV after FLAC conversion: {ex.Message}"); }
                    });
                }
                else
                {
                    Debug.WriteLine($"FFmpeg FLAC conversion failed. Exit Code: {process.ExitCode}\nOutput:\n{output}\nError:\n{error}");
                    // Conversion failed, keep the WAV file and maybe notify user?
                    // Reset the format in RecordingInfo back to WAV as FLAC failed?
                    Application.Current.Dispatcher.Invoke(() => {
                        MessageBox.Show($"Failed to convert recording to FLAC. The original WAV file has been kept.\n\nFFmpeg Error (see debug output for details):\n{error.Split('\n').FirstOrDefault()?.Trim()}", 
                                        "FLAC Conversion Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                        // Optionally change format back if needed, but requires RecordingInfo to be mutable or replaced
                        // recordingToConvert.RecordingFormat = "WAV"; 
                    });
                    // Keep the original WAV file path in RecordingInfo
                }
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2) // NativeErrorCode 2: File Not Found
        {
             Debug.WriteLine("ffmpeg.exe not found. Please ensure it is installed and in your system's PATH.");
             Application.Current.Dispatcher.Invoke(() => {
                 MessageBox.Show("Could not find ffmpeg.exe. FLAC conversion requires FFmpeg to be installed and accessible via the system PATH.",
                                 "FFmpeg Not Found", MessageBoxButton.OK, MessageBoxImage.Error);
                // Keep the original WAV file path in RecordingInfo
             });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error during FLAC conversion process: {ex.Message}");
             Application.Current.Dispatcher.Invoke(() => {
                 MessageBox.Show($"An unexpected error occurred during FLAC conversion:\n{ex.Message}",
                                 "FLAC Conversion Error", MessageBoxButton.OK, MessageBoxImage.Error);
                // Keep the original WAV file path in RecordingInfo
             });
        }
    }
    // ---------------------------------

    // --- Icon Helper Method ---
    private ImageSource? GetIconForProcess(int processId)
    {
        try
        {
            Process process = Process.GetProcessById(processId);
            string? exePath = process.MainModule?.FileName;

            if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
            {
                // Use fully qualified name for Icon to avoid potential ambiguity
                using (System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
                {
                    if (icon != null)
                    {
                        // Convert System.Drawing.Icon to WPF ImageSource
                        BitmapSource bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        bitmapSource.Freeze(); // Freeze for use on other threads if needed (good practice)
                        return bitmapSource;
                    }
                }
            }
        }
        catch (ArgumentException) { /* Process already exited */ }
        catch (Win32Exception ex) { Debug.WriteLine($"Error getting process info/icon (Win32) for PID {processId}: {ex.Message}"); }
        catch (InvalidOperationException ex) { Debug.WriteLine($"Error getting process info/icon (InvalidOp) for PID {processId}: {ex.Message}"); }
        catch (Exception ex) { Debug.WriteLine($"Error getting icon for PID {processId}: {ex.Message}"); }

        return null; // Return null if icon couldn't be obtained
    }
    // ------------------------
}