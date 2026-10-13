using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests;

/// <summary>
/// Tests that assert a wall-clock deadline run alone. Running them beside compile-heavy tests made a one-second grace
/// measure the other tests' CPU load instead of the behaviour under test (CI: a hung tree torn down at 4.9 s against a
/// 4 s deadline while an abandoned bounded compile was still burning cores).
/// </summary>
[CollectionDefinition(nameof(WallClockCollection), DisableParallelization = true)]
public sealed class WallClockCollection { }
