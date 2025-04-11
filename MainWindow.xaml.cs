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

namespace Recorder;

public class AudioSessionInfo
{
    public int ProcessId { get; set; }
    public string? ProcessName { get; set; }
    public string DisplayName => $"{ProcessName ?? "Unknown"} (PID: {ProcessId})";
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

    public MainWindow()
    {
        InitializeComponent();
        this.Closing += MainWindow_Closing;
        this.DataContext = this;
        RefreshAudioApplications();
        SetupPlaybackMonitorTimer();
        // TODO: Load history from storage?
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

        AppComboBox.Items.Clear();
        _mutedSessions = new Dictionary<int, bool>();

        List<AudioSessionInfo> sessions = new List<AudioSessionInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (device == null) return;

            var sessionManager = device.AudioSessionManager;
            if (sessionManager?.Sessions == null) return;

            for (int i = 0; i < sessionManager.Sessions.Count; i++)
            {
                using var session = sessionManager.Sessions[i];
                if (session == null) continue;

                try
                {
                    var processId = (int)session.GetProcessID;
                    if (processId == 0) continue; // Skip system sounds process
                    
                    // --- Added Debugging --- 
                    string processName = "<Error getting name>";
                    try { processName = Process.GetProcessById(processId)?.ProcessName ?? "<Name was null>"; } catch {}
                    Debug.WriteLine($"Found Session: PID={processId}, Name={processName}, State={session.State}");
                    // ----------------------

                    var process = Process.GetProcessById(processId);

                    // --- Modified State Check ---
                    if (process != null && 
                        !string.IsNullOrWhiteSpace(process.ProcessName) && 
                        (session.State == AudioSessionState.AudioSessionStateActive || session.State == AudioSessionState.AudioSessionStateInactive))
                    // -------------------------
                    {
                         if (!sessions.Any(s => s.ProcessId == processId))
                         {
                            sessions.Add(new AudioSessionInfo
                            {
                                ProcessId = processId,
                                ProcessName = process.ProcessName
                            });
                         }
                    }
                    process?.Dispose();
                }
                catch (ArgumentException) { }
                catch (Exception ex) { Debug.WriteLine($"Error getting process info: {ex.Message}"); }
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

        AppComboBox.DisplayMemberPath = "DisplayName";

        if (AppComboBox.Items.Count > 0)
        {
             AppComboBox.SelectedIndex = 0;
        }
        else
        {
            AutoStartCheckBox.IsChecked = false;
            AutoStartCheckBox.IsEnabled = false;
            StartButton.IsEnabled = false;
        }

        Debug.WriteLine($"Found {AppComboBox.Items.Count} applications with audio sessions.");

        if (AutoStartCheckBox.IsChecked == true && AppComboBox.SelectedItem != null)
        {
            _lastMonitoredSessionState = GetCurrentSessionState(((AudioSessionInfo)AppComboBox.SelectedItem).ProcessId);
            _playbackMonitorTimer?.Start();
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
        string fileExtension = selectedFormat.Equals("WAV", StringComparison.OrdinalIgnoreCase) ? ".wav" : ".mp3";
        // -------------------------

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

            // --- Create appropriate writer --- 
            if (selectedFormat.Equals("WAV", StringComparison.OrdinalIgnoreCase))
            {
                _wavWriter = new WaveFileWriter(_outputFilePath, _capture.WaveFormat);
                _mp3Writer = null; // Ensure other writer is null
                Debug.WriteLine("Using WaveFileWriter for WAV format.");
            }
            else // Default to MP3
            {
                _mp3Writer = new LameMP3FileWriter(_outputFilePath, _capture.WaveFormat, 192); 
                _wavWriter = null; // Ensure other writer is null
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
                    // --- Store format with the recording --- 
                    string formatUsed = System.IO.Path.GetExtension(tempFilePath).TrimStart('.').ToUpperInvariant();
                    var newRecording = new RecordingInfo { FilePath = tempFilePath, RecordingFormat = formatUsed };
                    // ---------------------------------------
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
        _playbackMonitorTimer?.Stop();
        StartRecording();
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
        if (RecordingsListView.SelectedItem is RecordingInfo selectedRecording)
        {
            if (!System.IO.File.Exists(selectedRecording.FilePath))
            {
                 MessageBox.Show($"The temporary file for this recording no longer exists:\n{selectedRecording.FilePath}", "Temporary File Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
                 FinishedRecordings.Remove(selectedRecording);
                 return;
            }

            // --- Set SaveFileDialog Filter based on recording format --- 
            string fileFilter;
            string defaultExt;
            if (selectedRecording.RecordingFormat.Equals("WAV", StringComparison.OrdinalIgnoreCase))
            {
                fileFilter = "WAV Audio File (*.wav)|*.wav";
                defaultExt = ".wav";
            }
            else // Default to MP3
            {
                 fileFilter = "MP3 Audio File (*.mp3)|*.mp3";
                 defaultExt = ".mp3";
            }
            // ----------------------------------------------------------

            string initialDirectory = System.IO.Path.GetDirectoryName(selectedRecording.FilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            // Ensure suggested filename has the correct extension
            string initialFileName = System.IO.Path.ChangeExtension(System.IO.Path.GetFileName(selectedRecording.FilePath), defaultExt);

            SaveFileDialog saveFileDialog = new SaveFileDialog();
            saveFileDialog.Filter = fileFilter;
            saveFileDialog.Title = "Save Recording As...";
            saveFileDialog.InitialDirectory = initialDirectory;
            saveFileDialog.FileName = initialFileName; 
            saveFileDialog.DefaultExt = defaultExt;

            if (saveFileDialog.ShowDialog() == true)
            {
                string destinationPath = saveFileDialog.FileName;
                try
                {
                    System.IO.File.Move(selectedRecording.FilePath, destinationPath, true);
                    Debug.WriteLine($"Moved {selectedRecording.FilePath} to {destinationPath}");

                    // Add to History (RecordingFormat is already set)
                    var savedInfo = new RecordingInfo { FilePath = destinationPath, RecordingFormat = selectedRecording.RecordingFormat }; 
                    SavedRecordings.Add(savedInfo);
                    FinishedRecordings.Remove(selectedRecording);

                    MessageBox.Show($"Recording saved successfully to:\n{destinationPath}", "Save Successful", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error moving file {selectedRecording.FilePath} to {destinationPath}: {ex.Message}");
                    MessageBox.Show($"Failed to save the file.\nError: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                 Debug.WriteLine("Save file dialog cancelled by user.");
            }
        }
        else
        {
             MessageBox.Show("Please select a recording from the list to save.", "No Recording Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateUI()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            bool controlsEnabled = !_isRecording;
            StartButton.IsEnabled = controlsEnabled && AppComboBox.SelectedItem != null;
            AppComboBox.IsEnabled = controlsEnabled;
            RefreshAppsButton.IsEnabled = controlsEnabled;
            AutoStartCheckBox.IsEnabled = controlsEnabled && AppComboBox.SelectedItem != null;

            StopButton.IsEnabled = _isRecording;
        });
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

    // --- INotifyPropertyChanged Implementation ---
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    // -----------------------------------------
}