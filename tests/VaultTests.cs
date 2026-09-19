using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using Shouldly;
using TheRoom.Config;
using TheRoom.Entities;

namespace TheRoom.Tests;

/// <summary>
/// The parkour vault's obstacle probe (Player.FindVault) on a bare physics world: a player
/// standing at the origin, facing -Z, with one box in front of them.
/// </summary>
public class VaultTests : TestClass
{
    public VaultTests(Node testScene) : base(testScene) { }

    // The body's centre: Player.tscn's capsule is 1.8 m tall, so the feet are on the floor at y=0.
    private static readonly Vector3 Centre = new(0f, 0.9f, 0f);

    private static readonly Tuning Tuning = GD.Load<Tuning>("res://tuning/tuning.tres");

    /// <summary>Builds a box of <paramref name="size"/> standing on the floor, its near face
    /// <paramref name="gap"/> metres in front of the capsule, probes once, and removes it.</summary>
    private async Task<(bool found, float height)> Probe(Vector3 size, float gap, bool isPlayer = false)
    {
        var world = new Node3D();
        TestScene.AddChild(world);
        try
        {
            PhysicsBody3D body = isPlayer ? new CharacterBody3D() : new StaticBody3D();
            body.Position = new Vector3(0f, size.Y / 2f, -(0.4f + gap + size.Z / 2f));
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            world.AddChild(body);
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.PhysicsFrame);
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.PhysicsFrame);

            var space = world.GetWorld3D().DirectSpaceState;
            var found = Player.FindVault(space, new Rid(), Centre, Vector3.Forward, Tuning, out var height);
            return (found, height);
        }
        finally
        {
            world.QueueFree();
        }
    }

    [Test]
    public async Task VaultsWaistHighCover()
    {
        var (found, height) = await Probe(new Vector3(3f, 1.1f, 0.6f), gap: 0.3f);
        found.ShouldBeTrue();
        height.ShouldBe(1.1f, 0.02f);
    }

    [Test]
    public async Task VaultsCoverAtTheHeightLimits()
    {
        (await Probe(new Vector3(3f, Tuning.VaultMinHeight + 0.05f, 1f), gap: 0.2f)).found.ShouldBeTrue();
        (await Probe(new Vector3(3f, Tuning.VaultMaxHeight - 0.05f, 1f), gap: 0.2f)).found.ShouldBeTrue();
    }

    [Test]
    public async Task DoesNotVaultACurb() =>
        (await Probe(new Vector3(3f, 0.3f, 1f), gap: 0.2f)).found.ShouldBeFalse();

    [Test]
    public async Task DoesNotVaultAWall() =>
        (await Probe(new Vector3(6f, 2.6f, 2.4f), gap: 0.2f)).found.ShouldBeFalse(); // a shipping container

    [Test]
    public async Task DoesNotVaultCoverOutOfReach() =>
        (await Probe(new Vector3(3f, 1f, 1f), gap: Tuning.VaultReach + 0.3f)).found.ShouldBeFalse();

    [Test]
    public async Task DoesNotVaultAnotherPlayer() =>
        (await Probe(new Vector3(0.8f, 1f, 0.8f), gap: 0.2f, isPlayer: true)).found.ShouldBeFalse();
}
