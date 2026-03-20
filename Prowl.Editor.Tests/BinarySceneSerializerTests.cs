// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Editor.Services;
using Prowl.Runtime.Resources;

using Xunit;

namespace Prowl.Editor.Tests;

/// <summary>
/// Tests for <see cref="BinarySceneSerializer"/> covering round-trip
/// serialization, file extension, and interoperability with
/// <see cref="JsonSceneSerializer"/>.
/// </summary>
public sealed class BinarySceneSerializerTests : IDisposable
{
    private readonly string _tempDir;

    public BinarySceneSerializerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ProwlBinaryScene_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void FileExtension_IsBscene()
    {
        var serializer = new BinarySceneSerializer();
        Assert.Equal(".bscene", serializer.FileExtension);
    }

    [Fact]
    public void Save_CreatesFile()
    {
        var serializer = new BinarySceneSerializer();
        var scene = new Scene { Name = "TestScene" };

        string path = Path.Combine(_tempDir, "test.bscene");
        serializer.Save(scene, path);

        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);
    }

    [Fact]
    public void Save_CreatesDirectory_WhenMissing()
    {
        var serializer = new BinarySceneSerializer();
        var scene = new Scene { Name = "SubDirScene" };

        string path = Path.Combine(_tempDir, "sub", "dir", "scene.bscene");
        serializer.Save(scene, path);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void SaveAndLoad_RoundTrips_EmptyScene()
    {
        var serializer = new BinarySceneSerializer();
        var scene = new Scene();

        string path = Path.Combine(_tempDir, "empty.bscene");
        serializer.Save(scene, path);

        Scene? loaded = serializer.Load(path);
        Assert.NotNull(loaded);
        Assert.Equal(0, loaded!.Count);
    }

    [Fact]
    public void Load_ReturnsNull_WhenFileNotFound()
    {
        var serializer = new BinarySceneSerializer();
        string path = Path.Combine(_tempDir, "nonexistent.bscene");

        Scene? loaded = serializer.Load(path);
        Assert.Null(loaded);
    }

    [Fact]
    public void Binary_IsSmallerThanJson()
    {
        var jsonSerializer = new JsonSceneSerializer();
        var binarySerializer = new BinarySceneSerializer();
        var scene = new Scene { Name = "SizeComparisonScene" };

        string jsonPath = Path.Combine(_tempDir, "compare.scene");
        string binaryPath = Path.Combine(_tempDir, "compare.bscene");

        jsonSerializer.Save(scene, jsonPath);
        binarySerializer.Save(scene, binaryPath);

        long jsonSize = new FileInfo(jsonPath).Length;
        long binarySize = new FileInfo(binaryPath).Length;

        Assert.True(binarySize <= jsonSize,
            $"Expected binary ({binarySize} bytes) to be no larger than JSON ({jsonSize} bytes).");
    }

    [Fact]
    public void JsonAndBinary_ProduceSameScene()
    {
        var jsonSerializer = new JsonSceneSerializer();
        var binarySerializer = new BinarySceneSerializer();
        var scene = new Scene { Name = "CrossFormatScene" };

        // Save with JSON, load with JSON, save as binary, load from binary
        string jsonPath = Path.Combine(_tempDir, "cross.scene");
        string binaryPath = Path.Combine(_tempDir, "cross.bscene");

        jsonSerializer.Save(scene, jsonPath);
        Scene? fromJson = jsonSerializer.Load(jsonPath);
        Assert.NotNull(fromJson);

        binarySerializer.Save(fromJson!, binaryPath);
        Scene? fromBinary = binarySerializer.Load(binaryPath);
        Assert.NotNull(fromBinary);

        Assert.Equal(fromJson!.Name, fromBinary!.Name);
        Assert.Equal(fromJson.Count, fromBinary.Count);
    }

    [Fact]
    public void EncodingMode_CanBeChanged()
    {
        var serializer = new BinarySceneSerializer
        {
            EncodingMode = Prowl.Echo.BinaryEncodingMode.Performance
        };
        var scene = new Scene();

        string path = Path.Combine(_tempDir, "perf.bscene");
        serializer.Save(scene, path);

        Scene? loaded = serializer.Load(path);
        Assert.NotNull(loaded);
        Assert.Equal(0, loaded!.Count);
    }
}
