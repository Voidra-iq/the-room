using System.Collections.Generic;
using System.Linq;
using Chickensoft.GoDotTest;
using Godot;
using Shouldly;
using TheRoom.Animation;
using TheRoom.Config;

namespace TheRoom.Tests;

/// <summary>
/// The contract that makes animations pluggable: every clip in the shared set targets bones by
/// their standard humanoid names, and every character model has those bones. A new model
/// imported without the humanoid retarget preset (assets/animations/humanoid/README.md) fails
/// here instead of T-posing in game.
/// </summary>
public class CharacterAnimationTests : TestClass
{
    public CharacterAnimationTests(Node testScene) : base(testScene) { }

    private static HumanoidAnimationSet DefaultSet() =>
        GD.Load<HumanoidAnimationSet>(HumanoidAnimationSet.DefaultPath);

    private static HumanoidAnimationSet ZainSet() =>
        GD.Load<HumanoidAnimationSet>(CharacterRegistry.GetOrDefault("zain").AnimationsPath);

    [Test]
    public void ZainActsOutHisDropKick()
    {
        CharacterRegistry.GetOrDefault("zain").Ability!.Id.ShouldBe("dropkick");
        var model = CharacterModel.Create(GD.Load<PackedScene>(CharacterModel.DefaultModelPath), ZainSet(), 1.8f);
        try
        {
            model.GetClip(CharacterModel.Clip.Ability).ShouldNotBeNull();
        }
        finally
        {
            model.Free();
        }
    }

    private static IEnumerable<(string Name, PackedScene Scene)> AllModels()
    {
        yield return ("default", GD.Load<PackedScene>(CharacterModel.DefaultModelPath));
        foreach (var def in CharacterRegistry.All.Values.Where(d => d.Model is not null))
            yield return (def.Id, def.Model!);
    }

    [Test]
    public void EveryClipTargetsBonesPresentOnEveryModel()
    {
        var set = DefaultSet();
        var zain = ZainSet();
        var clips = new[] { set.Idle, set.Run, set.Jump, set.JumpUp, set.Vault, set.LightAttack, set.HeavyAttack, set.Dodge, set.Death, zain.Ability }
            .Select(HumanoidAnimationSet.FirstClip)
            .Where(c => c is not null)
            .ToList();
        clips.Count.ShouldBeGreaterThanOrEqualTo(3); // run, jump, attack at minimum

        var missing = new List<string>();
        foreach (var (name, scene) in AllModels())
        {
            var root = scene.Instantiate<Node3D>();
            var skeleton = root.FindChildren("*", nameof(Skeleton3D), true, false).OfType<Skeleton3D>().Single();
            foreach (var clip in clips)
            {
                for (var t = 0; t < clip!.GetTrackCount(); t++)
                {
                    var path = clip.TrackGetPath(t);
                    var bone = path.GetConcatenatedSubNames();
                    if (path.GetName(path.GetNameCount() - 1) != "%" + skeleton.Name || skeleton.FindBone(bone) < 0)
                        missing.Add($"{name}: {path}");
                }
            }
            root.Free();
        }

        missing.Distinct().ShouldBeEmpty();
    }

    [Test]
    public void HeldPropAttachesToTheRightHand()
    {
        var model = CharacterModel.Create(GD.Load<PackedScene>(CharacterModel.DefaultModelPath), DefaultSet(), 1.8f,
            GD.Load<PackedScene>(CharacterModel.DefaultHeldPropPath));
        try
        {
            model.HeldProp.ShouldNotBeNull();
            var attachment = model.HeldProp!.GetParent().ShouldBeOfType<BoneAttachment3D>();
            attachment.BoneName.ShouldBe("RightHand");
            attachment.GetParent().ShouldBeOfType<Skeleton3D>();
        }
        finally
        {
            model.Free();
        }
    }

    /// <summary>The Golden Knife holder's knife is swapped in place (core/MatchServer.cs), and
    /// swapped back when the knife is lost.</summary>
    [Test]
    public void HeldPropSwapsForTheGoldenKnifeAndBack()
    {
        var model = CharacterModel.Create(GD.Load<PackedScene>(CharacterModel.DefaultModelPath), DefaultSet(), 1.8f,
            GD.Load<PackedScene>(CharacterModel.DefaultHeldPropPath));
        try
        {
            var hand = model.HeldProp!.GetParent();
            model.SetHeldProp(GD.Load<PackedScene>(TheRoom.Core.MatchServer.GoldenKnifePropPath));
            model.HeldProp!.Name.ToString().ShouldBe("GoldenKnife");
            model.HeldProp.GetParent().ShouldBe(hand);
            model.SetHeldProp(GD.Load<PackedScene>(CharacterModel.DefaultHeldPropPath));
            model.HeldProp!.Name.ToString().ShouldBe("Knife");
            hand.GetChildCount().ShouldBe(1); // the Golden Knife is gone, not stacked
        }
        finally
        {
            model.Free();
        }
    }

    /// <summary>The physics body does the vault's and the standing jump's lifting; if the clip's
    /// hips rose as well, the character would float above the capsule mid-air.</summary>
    [Test]
    public void JumpClipsNeverLiftTheHips()
    {
        var set = DefaultSet();
        var model = CharacterModel.Create(GD.Load<PackedScene>(CharacterModel.DefaultModelPath), set, 1.8f);
        try
        {
            // Measured against the source clip's first frame (standing): a slice may start mid-crouch.
            foreach (var (clip, source) in new[] { (CharacterModel.Clip.Jump, set.Jump), (CharacterModel.Clip.JumpUp, set.JumpUp), (CharacterModel.Clip.Vault, set.Vault) })
            {
                var standing = HipsTrack(HumanoidAnimationSet.FirstClip(source)!, out var sourceTrack).TrackGetKeyValue(sourceTrack, 0).AsVector3().Y;
                var animation = HipsTrack(model.GetClip(clip)!, out var track);
                for (var k = 0; k < animation.TrackGetKeyCount(track); k++)
                    animation.TrackGetKeyValue(track, k).AsVector3().Y.ShouldBeLessThanOrEqualTo(standing + 0.001f, $"{clip} key {k}");
            }
        }
        finally
        {
            model.Free();
        }
    }

    private static Godot.Animation HipsTrack(Godot.Animation animation, out int track)
    {
        for (track = 0; track < animation.GetTrackCount(); track++)
        {
            if (animation.TrackGetType(track) == Godot.Animation.TrackType.Position3D && animation.TrackGetPath(track).ToString().EndsWith(":Hips"))
                return animation;
        }
        throw new ShouldAssertException("no hips position track");
    }

    /// <summary>Regression: Zain's own set is a copy of the shared one plus his drop kick, and it
    /// missed the standing jump and vault when they were added, so nobody played them in game.
    /// Every character's set must build every shared clip.</summary>
    [Test]
    public void EveryCharacterSetHasEverySharedClip()
    {
        var missing = new List<string>();
        foreach (var def in CharacterRegistry.All.Values)
        {
            var path = string.IsNullOrEmpty(def.AnimationsPath) ? HumanoidAnimationSet.DefaultPath : def.AnimationsPath;
            var model = CharacterModel.Create(GD.Load<PackedScene>(CharacterModel.DefaultModelPath), def.Animations ?? GD.Load<HumanoidAnimationSet>(path), 1.8f);
            foreach (var clip in System.Enum.GetValues<CharacterModel.Clip>())
            {
                if (clip != CharacterModel.Clip.Ability && model.GetClip(clip) is null)
                    missing.Add($"{def.Id}: {clip}");
            }
            model.Free();
        }
        missing.ShouldBeEmpty();
    }

    [Test]
    public void BuiltModelHasEveryClipAndNoRootMotion()
    {
        var model = CharacterModel.Create(GD.Load<PackedScene>(CharacterModel.DefaultModelPath), DefaultSet(), 1.8f);
        try
        {
            // Ability is character-specific: the shared set has none, Zain's has the drop kick.
            foreach (var clip in System.Enum.GetValues<CharacterModel.Clip>())
            {
                if (clip == CharacterModel.Clip.Ability)
                    model.GetClip(clip).ShouldBeNull();
                else
                    model.GetClip(clip).ShouldNotBeNull($"clip {clip}");
            }

            var run = model.GetClip(CharacterModel.Clip.Run)!;
            run.LoopMode.ShouldBe(Godot.Animation.LoopModeEnum.Linear);
            for (var t = 0; t < run.GetTrackCount(); t++)
            {
                if (run.TrackGetType(t) != Godot.Animation.TrackType.Position3D)
                    continue;
                var first = run.TrackGetKeyValue(t, 0).AsVector3();
                var last = run.TrackGetKeyValue(t, run.TrackGetKeyCount(t) - 1).AsVector3();
                new Vector2(last.X - first.X, last.Z - first.Z).Length().ShouldBeLessThan(0.001f);
            }
        }
        finally
        {
            model.Free();
        }
    }
}
