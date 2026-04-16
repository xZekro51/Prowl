using BenchmarkDotNet.Attributes;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Microsoft.VSDiagnostics;

namespace BenchmarkSuite;
/// <summary>
/// A minimal MonoBehaviour that does lightweight work in Update, similar to Spinner in the StressTest.
/// </summary>
public class BenchSpinner : MonoBehaviour
{
    public Float3 RotationSpeed = new(30f, 60f, 15f);
    public override void Update()
    {
        GameObject.Transform.LocalEulerAngles += RotationSpeed * Time.DeltaTime;
    }
}

[CPUUsageDiagnoser]
public class UpdatePipelineBenchmark
{
    private Scene _scene;
    [Params(100, 1000, 5000)]
    public int ObjectCount;
    [GlobalSetup]
    public void Setup()
    {
        // Ensure Application.IsPlaying so ShouldExecuteGameplay returns true
        Application.IsPlaying = true;
        _scene = new Scene();
        for (int i = 0; i < ObjectCount; i++)
        {
            var go = new GameObject($"GO_{i}");
            var spinner = go.AddComponent<BenchSpinner>();
            spinner.RotationSpeed = new Float3(30f, 60f, 15f);
            _scene.Add(go);
        }

        _scene.Enable();
        // Run one update to trigger Start on all components
        _scene.Update();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scene?.Dispose();
        Application.IsPlaying = false;
    }

    [Benchmark]
    public void SceneUpdate()
    {
        _scene.Update();
    }
}
