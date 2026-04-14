// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Reflection;

using Prowl.Runtime;

using Xunit;

namespace Prowl.Runtime.Test;

/// <summary>
/// Tests for hotload attribute declarations and their usage contracts.
/// </summary>
public class HotloadAttributeTests
{
    [Fact]
    public void OnHotloadedAttribute_TargetsMethodsOnly()
    {
        var attr = typeof(OnHotloadedAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(AttributeTargets.Method, attr!.ValidOn);
        Assert.False(attr.AllowMultiple);
        Assert.True(attr.Inherited);
    }

    [Fact]
    public void OnCodeCleanupAttribute_TargetsMethodsOnly()
    {
        var attr = typeof(OnCodeCleanupAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(AttributeTargets.Method, attr!.ValidOn);
        Assert.False(attr.AllowMultiple);
        Assert.True(attr.Inherited);
    }

    [Fact]
    public void OnCodeInitializingAttribute_TargetsMethodsOnly()
    {
        var attr = typeof(OnCodeInitializingAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(AttributeTargets.Method, attr!.ValidOn);
        Assert.False(attr.AllowMultiple);
        Assert.True(attr.Inherited);
    }

    [Fact]
    public void AutoStaticsCleanupAttribute_TargetsClassesOnly()
    {
        var attr = typeof(AutoStaticsCleanupAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(AttributeTargets.Class, attr!.ValidOn);
        Assert.False(attr.AllowMultiple);
        Assert.False(attr.Inherited);
    }

    [Fact]
    public void PreserveOnHotloadAttribute_TargetsFieldsOnly()
    {
        var attr = typeof(PreserveOnHotloadAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(AttributeTargets.Field, attr!.ValidOn);
        Assert.False(attr.AllowMultiple);
        Assert.True(attr.Inherited);
    }

    [Fact]
    public void IHotloadUpgrader_HasOnHotloadUpgradeMethod()
    {
        var method = typeof(IHotloadUpgrader).GetMethod("OnHotloadUpgrade");
        Assert.NotNull(method);
        Assert.Single(method!.GetParameters());
        Assert.Equal(typeof(Echo.EchoObject), method.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void OnHotloadedAttribute_CanApplyToMethod()
    {
        var method = typeof(HotloadTestComponent).GetMethod("HandleHotload");
        Assert.NotNull(method);
        Assert.True(method!.IsDefined(typeof(OnHotloadedAttribute), true));
    }

    [Fact]
    public void OnCodeCleanupAttribute_CanApplyToMethod()
    {
        var method = typeof(HotloadTestComponent).GetMethod("Cleanup");
        Assert.NotNull(method);
        Assert.True(method!.IsDefined(typeof(OnCodeCleanupAttribute), true));
    }

    [Fact]
    public void AutoStaticsCleanupAttribute_CanApplyToClass()
    {
        Assert.True(typeof(AutoStaticsTestClass).IsDefined(typeof(AutoStaticsCleanupAttribute), false));
    }

    [Fact]
    public void PreserveOnHotloadAttribute_CanApplyToField()
    {
        var field = typeof(HotloadTestComponent).GetField("PreservedValue");
        Assert.NotNull(field);
        Assert.True(field!.IsDefined(typeof(PreserveOnHotloadAttribute), true));
    }

    // Test fixtures

    private class HotloadTestComponent : MonoBehaviour
    {
        [PreserveOnHotload]
        public float PreservedValue = 42f;

        [OnHotloaded]
        public void HandleHotload() { }

        [OnCodeCleanup]
        public void Cleanup() { }

        [OnCodeInitializing]
        public void InitCode() { }
    }

    [AutoStaticsCleanup]
    private class AutoStaticsTestClass
    {
        public static int Counter = 0;
        public static string? Name = "test";
    }
}
