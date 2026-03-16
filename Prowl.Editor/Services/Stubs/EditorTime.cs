// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Editor.Services;

/// <summary>
/// Default implementation of <see cref="IEditorTime"/> that tracks simulation
/// time in-process. When paused, <see cref="Tick"/> returns false unless a
/// single-step was requested.
/// </summary>
public sealed class EditorTime : IEditorTime
{
    private bool _playing;
    private bool _paused;
    private int _stepFrames;  // how many single-step frames remain

    public bool IsPlaying => _playing;
    public bool IsPaused => _paused;
    public float TimeScale { get; set; } = 1.0f;
    public float SimulationTime { get; private set; }
    public long FrameCount { get; private set; }
    public float DeltaTime { get; private set; }

    public void Play()
    {
        if (!_playing)
        {
            _playing = true;
            _paused = false;
            SimulationTime = 0f;
            FrameCount = 0;
            DeltaTime = 0f;
            _stepFrames = 0;
        }
        else
        {
            // Already playing — unpause
            _paused = false;
        }
    }

    public void Pause()
    {
        if (_playing)
            _paused = true;
    }

    public void Step()
    {
        if (_playing && _paused)
            _stepFrames++;
    }

    public void Stop()
    {
        _playing = false;
        _paused = false;
        SimulationTime = 0f;
        FrameCount = 0;
        DeltaTime = 0f;
        _stepFrames = 0;
    }

    public bool Tick(float realDelta)
    {
        if (!_playing) return false;

        if (_paused)
        {
            if (_stepFrames > 0)
            {
                _stepFrames--;
                // Fall through to advance one frame
            }
            else
            {
                DeltaTime = 0f;
                return false;
            }
        }

        float dt = realDelta * TimeScale;
        DeltaTime = dt;
        SimulationTime += dt;
        FrameCount++;
        return true;
    }
}
