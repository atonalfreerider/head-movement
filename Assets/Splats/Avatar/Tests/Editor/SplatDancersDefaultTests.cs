using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using D = HmSplats.SplatDancersDefault;

namespace HmSplats.Tests
{
    /// <summary>Edit-mode tests of the splat dancers' default at a capture load (SplatDancersDefault: VIEWER_SPEC 3.2a,
    /// docs/SPLAT_DANCERS.md "Default per capture"), plus a check of the capture.json flags present in StreamingAssets (git-ignored data).</summary>
    public class SplatDancersDefaultTests
    {
        static string Path12 => D.NoPath(GraphicsDeviceType.Direct3D12, true, D.Host.Editor);

        static D.Inputs Editor(bool flag, bool data = true, string noPath = null, bool handSet = false, bool handOn = false) => new()
        {
            Host = D.Host.Editor, HandSet = handSet, HandOn = handOn, CaptureFlag = flag, HasSplatData = data, NoPath = noPath ?? Path12
        };

        static D.Inputs Player(D.Host host, bool flag, bool vr = false, bool given = false, bool settingsOn = true, bool data = true, string noPath = "") => new()
        {
            Host = host, SettingsGiven = given, SettingsOn = settingsOn, Vr = vr, CaptureFlag = flag, HasSplatData = data, NoPath = noPath
        };

        [Test]
        public void NoPath_OnlyModernApisWithComputeDraw()
        {
            Assert.AreEqual("", D.NoPath(GraphicsDeviceType.Direct3D12, true, D.Host.Editor));
            Assert.AreEqual("", D.NoPath(GraphicsDeviceType.Vulkan, true, D.Host.Quest));
            Assert.AreEqual("", D.NoPath(GraphicsDeviceType.Metal, true, D.Host.Editor));
            StringAssert.Contains("start the editor with -force-d3d12", D.NoPath(GraphicsDeviceType.Direct3D11, true, D.Host.Editor));
            StringAssert.Contains("start the exe with -force-d3d12", D.NoPath(GraphicsDeviceType.Direct3D11, true, D.Host.WindowsPlayer));
            StringAssert.StartsWith("GLES", D.NoPath(GraphicsDeviceType.OpenGLES3, true, D.Host.Quest));
            StringAssert.StartsWith("GLES", D.NoPath(GraphicsDeviceType.OpenGLCore, true, D.Host.Editor));
            StringAssert.Contains("no compute", D.NoPath(GraphicsDeviceType.Direct3D12, false, D.Host.Editor));
            Assert.IsNotEmpty(D.NoPath(GraphicsDeviceType.Null, true, D.Host.Editor), "no device (batch mode): never on by default");
        }

        [Test]
        public void Editor_AFlaggedCaptureIsOnByDefault()
        {
            D.Decision d = D.Decide(Editor(flag: true));
            Assert.IsTrue(d.On, d.ToString());
            Assert.IsFalse(d.Refused);
            StringAssert.Contains("capture default", d.Why);
            Assert.IsTrue(D.Decide(Editor(true, noPath: D.NoPath(GraphicsDeviceType.Vulkan, true, D.Host.Editor))).On, "Vulkan");
        }

        [Test]
        public void Editor_CapturesWithoutTheFlagStayOff()
        {
            // the recap / every other capture with splat data: no capture.json key = the desktop default, as before
            D.Decision d = D.Decide(Editor(flag: false));
            Assert.IsFalse(d.On, d.ToString());
            Assert.IsFalse(d.Refused);
            Assert.IsFalse(D.Decide(Editor(false, data: false)).On);
        }

        [Test]
        public void Editor_TheFlagWithoutSplatDataIsOff()
        {
            D.Decision d = D.Decide(Editor(flag: true, data: false));
            Assert.IsFalse(d.On, d.ToString());
            Assert.IsFalse(d.Refused, "nothing to draw: no refusal to report");
        }

        [Test]
        public void Editor_D3D11RefusesTheFlagOnceWithTheFix()
        {
            D.Decision d = D.Decide(Editor(flag: true, noPath: D.NoPath(GraphicsDeviceType.Direct3D11, true, D.Host.Editor)));
            Assert.IsFalse(d.On, d.ToString());
            Assert.IsTrue(d.Refused);
            StringAssert.Contains("-force-d3d12", d.Why);
            Assert.IsFalse(D.Decide(Editor(true, noPath: D.NoPath(GraphicsDeviceType.Direct3D12, false, D.Host.Editor))).On, "no compute shaders");
            Assert.IsFalse(D.Decide(Editor(false, noPath: D.NoPath(GraphicsDeviceType.Direct3D11, true, D.Host.Editor))).Refused,
                "an unflagged capture on D3D11 asks for nothing: no log line");
        }

        [Test]
        public void Editor_TheHandToggleWinsBothWays()
        {
            Assert.IsFalse(D.Decide(Editor(flag: true, handSet: true, handOn: false)).On, "hm_splatdancers off: a flagged capture stays off");
            Assert.IsTrue(D.Decide(Editor(flag: false, handSet: true, handOn: true)).On, "hm_splatdancers on: every capture with splats (as before)");
            Assert.IsTrue(D.Decide(Editor(flag: true, handSet: true, handOn: true)).On);
            // the hand toggle keeps today's behaviour exactly: no API test of its own (as hm_splatdancers on always did)
            D.Decision d11 = D.Decide(Editor(flag: true, noPath: D.NoPath(GraphicsDeviceType.Direct3D11, true, D.Host.Editor), handSet: true, handOn: true));
            Assert.IsTrue(d11.On);
            Assert.IsFalse(d11.Refused);
        }

        [Test]
        public void WindowsPlayer_DesktopOnForAFlaggedCaptureOnly()
        {
            Assert.IsTrue(D.Decide(Player(D.Host.WindowsPlayer, flag: true)).On, "desktop, flagged");
            Assert.IsFalse(D.Decide(Player(D.Host.WindowsPlayer, flag: false)).On, "desktop, no flag: off as before");
            Assert.IsFalse(D.Decide(Player(D.Host.WindowsPlayer, flag: true, data: false)).On, "desktop, flagged without splats/");
            Assert.IsTrue(D.Decide(Player(D.Host.WindowsPlayer, flag: false, vr: true)).On, "VR: on as before");
            Assert.IsFalse(D.Decide(Player(D.Host.WindowsPlayer, flag: true, given: true, settingsOn: false)).On, "-hmsplats off wins");
            Assert.IsTrue(D.Decide(Player(D.Host.WindowsPlayer, flag: false, given: true, settingsOn: true)).On, "-hmsplats on wins");
            D.Decision d11 = D.Decide(Player(D.Host.WindowsPlayer, flag: true, noPath: D.NoPath(GraphicsDeviceType.Direct3D11, true, D.Host.WindowsPlayer)));
            Assert.IsFalse(d11.On);
            Assert.IsTrue(d11.Refused);
        }

        [Test]
        public void Quest_Unchanged()
        {
            foreach (bool flag in new[] { false, true })
            {
                Assert.IsTrue(D.Decide(Player(D.Host.Quest, flag, settingsOn: true)).On, $"flag {flag}: on unless given off");
                Assert.IsFalse(D.Decide(Player(D.Host.Quest, flag, given: true, settingsOn: false)).On, $"flag {flag}: -hmsplats off");
                Assert.IsFalse(D.Decide(Player(D.Host.Quest, flag, noPath: D.NoPath(GraphicsDeviceType.OpenGLES3, true, D.Host.Quest))).On, $"flag {flag}: GLES");
                Assert.IsTrue(D.Decide(Player(D.Host.Quest, flag, data: false)).On, $"flag {flag}: the tier is on, a capture without splats/ draws its avatars");
            }
        }

        [Test]
        public void CaptureFlags_ValidWhenPresent()
        {
            // every capture.json in StreamingAssets with a splat_dancers key: "on" / "off", and "on" only with splat data
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
    }
}
