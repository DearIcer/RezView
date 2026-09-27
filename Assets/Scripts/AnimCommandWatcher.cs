using System;
using System.Globalization;
using System.IO;
using UnityEngine;

// Polls the anim-command.txt file written by the WPF preview window (next to the
// .cfan package) and switches the playback state accordingly, and writes the current
// playback status back to anim-status.txt (same poll cadence) so the preview window
// can show progress.
//
// Commands (single line, space separated):
//   bind          stop playback and restore the bind pose
//   track <i>     cross-fade to track i and restart it (a bare number is accepted too)
//   play / pause  resume / freeze the current track
//   seek <sec>    jump to a time within the current track
//
// Status file (single line):
//   bind -1 0 0.00 0.00
//   track <i> <playing 0|1> <timeSeconds> <lengthSeconds>
internal sealed class AnimCommandWatcher : MonoBehaviour
{
    private const float PollIntervalSeconds = 0.1f;
    private const float CrossFadeSeconds = 0.2f;

    private string _commandFilePath;
    private string _statusFilePath;
    private Animation _animation;
    private Transform[] _bones;
    private Vector3[] _bindPositions;
    private Quaternion[] _bindRotations;
    private Vector3[] _bindScales;
    private int _trackCount;
    private string _lastCommand;
    private int _currentTrack = -1;
    private bool _paused;

    public static AnimCommandWatcher Attach(
        GameObject host,
        string commandFilePath,
        Animation animation,
        Transform[] bones,
        int trackCount,
        int initialTrack)
    {
        var watcher = host.AddComponent<AnimCommandWatcher>();
        watcher._commandFilePath = commandFilePath;
        watcher._statusFilePath = Path.Combine(
            Path.GetDirectoryName(commandFilePath) ?? string.Empty, "anim-status.txt");
        watcher._animation = animation;
        watcher._bones = bones;
        watcher._trackCount = trackCount;
        watcher._currentTrack = initialTrack;

        watcher._bindPositions = new Vector3[bones.Length];
        watcher._bindRotations = new Quaternion[bones.Length];
        watcher._bindScales = new Vector3[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            watcher._bindPositions[i] = bones[i].localPosition;
            watcher._bindRotations[i] = bones[i].localRotation;
            watcher._bindScales[i] = bones[i].localScale;
        }

        return watcher;
    }

    private void Start()
    {
        Poll();
        InvokeRepeating(nameof(Poll), PollIntervalSeconds, PollIntervalSeconds);
    }

    private void Poll()
    {
        PollCommand();
        WriteStatus();
    }

    private void PollCommand()
    {
        string command;
        try
        {
            if (string.IsNullOrEmpty(_commandFilePath) || !File.Exists(_commandFilePath))
            {
                return;
            }

            command = File.ReadAllText(_commandFilePath).Trim();
        }
        catch
        {
            // The WPF side writes atomically, but tolerate any transient read failure.
            return;
        }

        if (command.Length == 0 || string.Equals(command, _lastCommand, StringComparison.Ordinal))
        {
            return;
        }

        _lastCommand = command;
        ApplyCommand(command);
    }

    private void ApplyCommand(string command)
    {
        if (_animation == null)
        {
            return;
        }

        string[] parts = command.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return;
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "bind":
                _currentTrack = -1;
                _animation.Stop();
                ResetBindPose();
                break;
            case "track" when parts.Length >= 2 && TryParseTrackIndex(parts[1], out int trackIndex):
                SwitchTrack(trackIndex);
                break;
            case "play":
                _paused = false;
                AnimationState playing = CurrentState();
                if (playing != null)
                {
                    playing.speed = 1f;
                    _animation.Play($"anim_{_currentTrack}");
                }

                break;
            case "pause":
                _paused = true;
                AnimationState paused = CurrentState();
                if (paused != null)
                {
                    paused.speed = 0f;
                }

                break;
            case "seek" when parts.Length >= 2 &&
                             float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float seekTime):
                SeekTo(seekTime);
                break;
            default:
                // Backwards compatibility: a bare number is a track switch.
                if (TryParseTrackIndex(parts[0], out int bareTrackIndex))
                {
                    SwitchTrack(bareTrackIndex);
                }

                break;
        }
    }

    private void SwitchTrack(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= _trackCount)
        {
            return;
        }

        // Clips only touch the nodes they animate; reset first so tracks that omit a
        // node do not inherit the previous track's pose for it.
        ResetBindPose();
        _currentTrack = trackIndex;

        string stateName = $"anim_{trackIndex}";
        _animation.CrossFade(stateName, CrossFadeSeconds);
        AnimationState state = _animation[stateName];
        state.time = 0f;
        state.speed = _paused ? 0f : 1f;
        if (_paused)
        {
            _animation.Sample();
        }
    }

    private void SeekTo(float time)
    {
        AnimationState state = CurrentState();
        if (state == null)
        {
            return;
        }

        state.enabled = true;
        state.time = Mathf.Clamp(time, 0f, state.length);
        state.speed = _paused ? 0f : 1f;
        // Force an evaluation so the new pose is visible even while paused.
        _animation.Sample();
    }

    private AnimationState CurrentState()
    {
        return _currentTrack >= 0 && _currentTrack < _trackCount ? _animation[$"anim_{_currentTrack}"] : null;
    }

    private void WriteStatus()
    {
        string line;
        AnimationState state = CurrentState();
        if (state == null)
        {
            line = "bind -1 0 0.00 0.00";
        }
        else
        {
            float length = state.length;
            float time = length > 0.0001f ? Mathf.Repeat(state.time, length) : 0f;
            bool playing = !_paused && _animation.IsPlaying($"anim_{_currentTrack}");
            line = string.Format(
                CultureInfo.InvariantCulture,
                "track {0} {1} {2:F3} {3:F3}",
                _currentTrack,
                playing ? 1 : 0,
                time,
                length);
        }

        try
        {
            string tempPath = _statusFilePath + ".tmp";
            File.WriteAllText(tempPath, line);
            if (File.Exists(_statusFilePath))
            {
                File.Delete(_statusFilePath);
            }

            File.Move(tempPath, _statusFilePath);
        }
        catch
        {
            // Best-effort status reporting; the preview window keeps its last values.
        }
    }

    private bool TryParseTrackIndex(string token, out int trackIndex)
    {
        return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out trackIndex);
    }

    private void ResetBindPose()
    {
        for (int i = 0; i < _bones.Length; i++)
        {
            _bones[i].localPosition = _bindPositions[i];
            _bones[i].localRotation = _bindRotations[i];
            _bones[i].localScale = _bindScales[i];
        }
    }
}
