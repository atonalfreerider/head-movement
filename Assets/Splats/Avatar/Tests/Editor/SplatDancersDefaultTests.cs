using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using D = HmSplats.SplatDancersDefault;

namespace HmSplats.Tests
{
    /// <summary>Edit-mode tests of how the dancers of a capture are drawn (SplatDancersDefault: VIEWER_SPEC 3.2a, docs/SPLAT_DANCERS.md
    /// "Splats always"; owner rule 2026-10-23: never the mesh avatars when splat avatars are available), plus checks of the capture data
    /// present in StreamingAssets (git-ignored): the capture.json flags and the archived mesh-avatar files.</summary>
    public class SplatDancersDefaultTests
    {
        static string Path12 => D.NoPath(GraphicsDeviceType.Direct3D12, true, D.Host.Editor);
        static string Path11(D.Host h) => D.NoPath(GraphicsDeviceType.Direct3D11, true, h);

        static D.Inputs In(D.Host host, bool data, string noPath = null, bool flag = false, bool handSet = false, bool handOn = false,
            bool given = false, bool settingsOn = true, bool vr = false) => new()
        {
            Host = host, HasSplatData = data, NoPath = noPath ?? Path12, CaptureFlag = flag, HandSet = handSet, HandOn = handOn,
            SettingsGiven = given, SettingsOn = settingsOn, Vr = vr
        };

        static readonly D.Host[] Hosts = { D.Host.Editor, D.Host.WindowsPlayer, D.Host.Quest };

        [Test]
        public void NoPath_OnlyModernApisWithComputeDraw()
        {
            Assert.AreEqual("", D.NoPath(GraphicsDeviceType.Direct3D12, true, D.Host.Editor));
            Assert.AreEqual("", D.NoPath(GraphicsDeviceType.Vulkan, true, D.Host.Quest));
            Assert.AreEqual("", D.NoPath(GraphicsDeviceType.Metal, true, D.Host.Editor));
            StringAssert.Contains("start the editor with -force-d3d12", Path11(D.Host.Editor));
            StringAssert.Contains("start the exe with -force-d3d12", Path11(D.Host.WindowsPlayer));
            StringAssert.StartsWith("GLES", D.NoPath(GraphicsDeviceType.OpenGLES3, true, D.Host.Quest));
            StringAssert.StartsWith("GLES", D.NoPath(GraphicsDeviceType.OpenGLCore, true, D.Host.Editor));
            StringAssert.Contains("no compute", D.NoPath(GraphicsDeviceType.Direct3D12, false, D.Host.Editor));
            Assert.IsNotEmpty(D.NoPath(GraphicsDeviceType.Null, true, D.Host.Editor), "no device (batch mode): never splats");
            foreach (D.Host h in Hosts) StringAssert.DoesNotContain("avatars", Path11(h), "the fallback is never the avatars");
        }

        [Test]
        public void SplatData_IsAlwaysDrawnAsSplats_OnEveryHost()
        {
            foreach (D.Host h in Hosts)
            {
                foreach (bool flag in new[] { false, true })
                {
                    D.Decision d = D.Decide(In(h, data: true, noPath: h == D.Host.Quest ? D.NoPath(GraphicsDeviceType.Vulkan, true, h) : Path12, flag: flag));
                    Assert.IsTrue(d.On, $"{h}, flag {flag}: {d}");
                    Assert.IsFalse(d.Refused);
                }
            }

            Assert.IsTrue(D.Decide(In(D.Host.WindowsPlayer, true, vr: true)).On, "PC VR");
            Assert.IsTrue(D.Decide(In(D.Host.WindowsPlayer, true, vr: false)).On, "the Windows desktop too (no flag needed)");
        }

        [Test]
        public void NothingSwitchesASplatCaptureBackToTheMeshAvatars()
        {
            // an "off" the editor session still holds (SessionState of the old hand toggle), a player's "splats": false / -hmsplats off
            D.Decision hand = D.Decide(In(D.Host.Editor, true, handSet: true, handOn: false));
            Assert.IsTrue(hand.On, hand.ToString());
            StringAssert.Contains("ignored", hand.Why);
            foreach (D.Host h in new[] { D.Host.WindowsPlayer, D.Host.Quest })
            {
                D.Decision off = D.Decide(In(h, true, given: true, settingsOn: false));
                Assert.IsTrue(off.On, $"{h}: {off}");
                StringAssert.Contains("ignored", off.Why);
            }
        }

        [Test]
        public void ADeviceThatCannotDraw_ShowsTheSkeleton_NeverTheAvatar()
        {
            foreach (D.Host h in Hosts)
            {
                foreach (string noPath in new[] { Path11(h), D.NoPath(GraphicsDeviceType.OpenGLES3, true, h), D.NoPath(GraphicsDeviceType.Direct3D12, false, h) })
                {
                    D.Decision d = D.Decide(In(h, data: true, noPath: noPath));
                    Assert.IsFalse(d.On, $"{h} {noPath}: {d}");
                    Assert.IsTrue(d.Refused, "reported: log line + on-screen note");
                    StringAssert.Contains("skeleton", d.Why);
                    Assert.AreEqual(D.Look.Skeleton, D.DancerLook(true, canDraw: false), $"{h} {noPath}");
                }
            }

            // the players' 3-s rendering watchdog gave up: skeleton too
            Assert.AreEqual(D.Look.Skeleton, D.DancerLook(true, canDraw: true, fellBack: true));
            Assert.IsFalse(D.MeshAvatarDrawn(true));
        }

        [Test]
        public void TheNote_NamesTheFix()
        {
            string n = D.Note(Path11(D.Host.Editor), D.Host.Editor);
            StringAssert.StartsWith("splat dancers need D3D12: start the editor with -force-d3d12", n);
            StringAssert.Contains("skeleton lines", n);
            StringAssert.Contains("start the exe with -force-d3d12", D.Note(Path11(D.Host.WindowsPlayer), D.Host.WindowsPlayer));
            StringAssert.Contains("GLES", D.Note(D.NoPath(GraphicsDeviceType.OpenGLES3, true, D.Host.Quest), D.Host.Quest));
            Assert.AreEqual("", D.Note("", D.Host.Editor), "nothing to say when the device can draw");
        }

        [Test]
        public void NoSplatData_KeepsTheMeshAvatars()
        {
            foreach (D.Host h in Hosts)
            {
                foreach (bool flag in new[] { false, true })
                {
                    D.Decision d = D.Decide(In(h, data: false, flag: flag));
                    Assert.IsFalse(d.On, $"{h}: {d}");
                    Assert.IsFalse(d.Refused, "nothing to draw: no refusal to report");
                    StringAssert.Contains("mesh avatars", d.Why);
                    D.Decision d11 = D.Decide(In(h, data: false, noPath: Path11(h)));
                    Assert.IsFalse(d11.Refused, "a capture without splats on D3D11 asks for nothing: no log line");
                }
            }

            Assert.AreEqual(D.Look.MeshAvatar, D.DancerLook(false, canDraw: true));
            Assert.AreEqual(D.Look.MeshAvatar, D.DancerLook(false, canDraw: false), "no splat data: the mesh avatar whatever the device");
            Assert.IsTrue(D.MeshAvatarDrawn(false));
            Assert.AreEqual(D.Look.Splats, D.DancerLook(true, canDraw: true));
        }

        [Test]
        public void CaptureFlags_ValidWhenPresent()
        {
            // every capture.json in StreamingAssets with a splat_dancers key (read for the log only since 2026-10-23): "on" / "off", and
            // "on" only with splat data
            string root = Application.streamingAssetsPath;
            string[] manifests = Directory.Exists(root) ? Directory.GetFiles(root, "capture.json", SearchOption.AllDirectories) : Array.Empty<string>();
            int flagged = 0;
            foreach (string m in manifests)
            {
                JToken v = JObject.Parse(File.ReadAllText(m))["splat_dancers"];
                if (v == null) continue;
                string s = ((string)v ?? "").Trim().ToLowerInvariant();
                Assert.That(s, Is.EqualTo("on").Or.EqualTo("off"), $"{m}: splat_dancers");
                if (s != "on") continue;
                string folder = Path.GetDirectoryName(m) ?? "";
                Assert.IsTrue(SplatAvatarData.JsonPath(folder, "lead") != null || SplatAvatarData.JsonPath(folder, "follow") != null,
                    $"{m}: splat_dancers on without splats/<role>.json");
                flagged++;
            }

            if (flagged == 0) Assert.Ignore("no capture in StreamingAssets asks for its splat dancers");
        }

        [Test]
        public void SplatData_ANamedSetAloneCounts()
        {
            // a data folder that carries only a distilled set (splats/<set>/<role>.json, no full file) still has a splat dancer: it is
            // drawn from that set (SplatDancers.Rebuild), never as its mesh avatar (CaptureManifest.HasSplatAvatar counts the same files)
            string folder = Path.Combine(Path.GetTempPath(), "hm_splatset_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(Path.Combine(folder, "splats", "quest_25k"));
                Directory.CreateDirectory(Path.Combine(folder, "splats", "a_empty"));
                Assert.IsNull(SplatAvatarData.AnySetJsonPath(folder, "follow"), "no set file yet");
                string set = Path.Combine(folder, "splats", "quest_25k", "follow.json");
                File.WriteAllText(set, "{}");
                Assert.IsNull(SplatAvatarData.JsonPath(folder, "follow"), "no full file");
                Assert.IsNull(SplatAvatarData.JsonPath(folder, "follow", "", out _), "the desktop's set ('') falls back to the full file only");
                Assert.AreEqual(set, SplatAvatarData.AnySetJsonPath(folder, "follow"));
                Assert.AreEqual(set, SplatAvatarData.JsonPath(folder, "follow", "quest_25k", out bool fromSet));
                Assert.IsTrue(fromSet);
                Assert.IsNull(SplatAvatarData.AnySetJsonPath(folder, "lead"), "the lead has no splat data");
                Assert.IsNull(SplatAvatarData.AnySetJsonPath(Path.Combine(folder, "missing"), "lead"), "no splats/ folder");
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        [Test]
        public void SplatCaptures_CarryNoMeshOnlyFiles()
        {
            // "fully archive them": a capture with splats/<role>.json has no <role>_albedo.png / smplx_<role>_face.bin (and no shoes.json
            // when both dancers have splats) in StreamingAssets - dancecap export_unity archive-avatars moved them to the take's archive
            string root = Application.streamingAssetsPath;
            int checkedCaptures = 0;
            foreach (string folder in Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>())
            {
                bool lead = SplatAvatarData.JsonPath(folder, "lead") != null || SplatAvatarData.AnySetJsonPath(folder, "lead") != null;
                bool follow = SplatAvatarData.JsonPath(folder, "follow") != null || SplatAvatarData.AnySetJsonPath(folder, "follow") != null;
                if (!lead && !follow) continue;
                checkedCaptures++;
                foreach ((string role, bool has) in new[] { ("lead", lead), ("follow", follow) })
                {
                    if (!has) continue;
                    foreach (string name in new[] { $"{role}_albedo.png", $"smplx_{role}_face.bin" })
                    {
                        Assert.IsFalse(File.Exists(Path.Combine(folder, name)), $"{Path.GetFileName(folder)}/{name}: mesh-only, should be archived");
                        Assert.IsFalse(File.Exists(Path.Combine(folder, name + ".meta")), $"{Path.GetFileName(folder)}/{name}.meta left behind");
                    }
                }

                if (lead && follow) Assert.IsFalse(File.Exists(Path.Combine(folder, "shoes.json")), $"{Path.GetFileName(folder)}/shoes.json: mesh-only");
                // what the rig, the splat posing, the hair simulation and the layers need stays
                foreach (string keep in new[] { "smplx_lead.bin", "smplx_follow.bin", "smplx_lead_skin.bin", "smplx_follow_skin.bin", "capture.json" })
                    Assert.IsTrue(File.Exists(Path.Combine(folder, keep)), $"{Path.GetFileName(folder)}/{keep} must stay");
            }

            if (checkedCaptures == 0) Assert.Ignore("no capture with splats/ in StreamingAssets");
        }
    }
}
