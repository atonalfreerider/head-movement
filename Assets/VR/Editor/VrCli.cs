using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

namespace HmVr.Editor
{
    /// <summary>
    /// CLI for the VR mode in the editor (Play mode; Tools/playtest_vr.ps1 drives it):
    ///
    ///   unity command hm_vr --action state                                  # everything: mode, rig, pointers, menu, hand panel, captions, film
    ///   unity command hm_vr --action enter [--flag true]                    # enter VR mode; --flag true = simulated hands (the default without a headset)
    ///   unity command hm_vr --action exit
    ///   unity command hm_vr --action widgets                                # every live widget: id, panel, world centre, size in mm, enabled, hovered
    ///   unity command hm_vr --action aim --hand right --widget play:demo --value 0       # point at a widget (value = pinch 0..1)
    ///   unity command hm_vr --action poke --hand right --widget play --depth 0.06        # fingertip in front of / pushed into a widget (m in front: 0.06 = hover, -0.01 = pressed)
    ///   unity command hm_vr --action palm --hand left --flag true                         # raise / lower a palm-up hand
    ///   unity command hm_vr --action pinch --hand right --value 1
    ///   unity command hm_vr --action hold --hand left --value 0.15 --pinch 1              # hand held out sideways (m right of the head), pinching: snap turn
    ///   unity command hm_vr --action rest --hand right | look --widget id | head --yaw 30 --pitch 0 | dominant --hand left | lost --hand right --flag true
    ///   unity command hm_vr --action selftest                                             # the full scripted scenario; poll with --action result
    ///
    /// Everything runs on simulated hands (HmVr.SimInputSource) - the same recognisers and panels a headset's hand tracking drives.
    /// </summary>
    public static class VrCli
    {
        [CliCommand("hm_vr", "VR mode (Play mode, simulated hands): state | enter | exit | widgets | aim | poke | palm | pinch | hold | rest | look | head | dominant | lost | selftest | result")]
        public static string Vr(
            [CliArg("action", "state | enter | exit | widgets | aim | poke | palm | pinch | hold | handat | rest | look | head | dominant | lost | instant | display | place | hair | tier | selftest | result")] string action = "state",
            [CliArg("hand", "left | right")] string hand = "right",
            [CliArg("widget", "widget id (see widgets)")] string widget = null,
            [CliArg("value", "pinch 0..1 / lateral metres / yaw")] float value = 0f,
            [CliArg("pinch", "hold: pinch 0..1")] float pinch = 0f,
            [CliArg("depth", "poke: metres in front of the widget")] float depth = 0.06f,
            [CliArg("u", "aim at a slider: position 0..1 along it")] float u = -1f,
            [CliArg("yaw", "head yaw (degrees, world)")] float yaw = 0f,
            [CliArg("pitch", "head pitch (degrees, down positive)")] float pitch = 0f,
            [CliArg("flag", "enter: simulated; palm: raised; instant/lost: on")] bool flag = true,
            [CliArg("x", "handat: metres right of the head")] float x = 0f,
            [CliArg("y", "handat: metres above the head (negative = below)")] float y = -0.35f,
            [CliArg("z", "handat: metres in front of the head")] float z = 0.45f)
        {
            string a = (action ?? "state").Trim().ToLowerInvariant();
            if (!EditorApplication.isPlaying && a != "state") throw new InvalidOperationException("enter Play mode first (unity command editor_play)");
            Hand h = string.Equals(hand, "left", StringComparison.OrdinalIgnoreCase) ? Hand.Left : Hand.Right;
            VrMode mode = VrMode.Instance;
            switch (a)
            {
                case "state":
                    break;
                case "enter":
                    Need(mode).Enter(flag);
                    break;
                case "exit":
                    Need(mode).Exit("cli");
                    break;
                case "widgets":
                    return Json(new Dictionary<string, object> { ["widgets"] = Widgets() });
                case "aim":
                {
                    SimInputSource sim = Sim();
                    VrWidget w = FindWidget(widget);
                    Vector3 p = w.WorldCentre;
                    if (u >= 0f && w.Kind == WidgetKind.Slider) p = w.Panel.Content.TransformPoint(new Vector3(w.RectMm.xMin + u * w.RectMm.width, w.RectMm.center.y, 0f));
                    sim.AimAt(h, p, value);
                    break;
                }
                case "poke":
                {
                    SimInputSource sim = Sim();
                    VrWidget w = FindWidget(widget);
                    Vector3 p = w.WorldCentre;
                    if (u >= 0f && w.Kind == WidgetKind.Slider) p = w.Panel.Content.TransformPoint(new Vector3(w.RectMm.xMin + u * w.RectMm.width, w.RectMm.center.y, 0f));
                    sim.PokeAt(h, p, w.Panel.Plane.Normal, depth);
                    break;
                }
                case "palm":
                    Sim().PalmUp(h, flag);
                    break;
                case "pinch":
                    Sim().SetPinch(h, value);
                    break;
                case "hold":
                    Sim().HoldOut(h, value, pinch);
                    break;
                case "rest":
                    Sim().Rest(h);
                    break;
                case "look":
                    Sim().LookAt(FindWidget(widget).WorldCentre);
                    break;
                case "head":
                {
                    SimInputSource sim = Sim();
                    sim.HeadYawLocalDeg = yaw - VrRig.Instance.transform.eulerAngles.y;
                    sim.HeadPitchLocalDeg = pitch;
                    break;
                }
                case "dominant":
                    Need(mode).Session.Input.Dominant = h;
                    break;
                case "lost":
                    Sim().SetTracked(h, !flag);
                    break;
                case "instant":
                    Sim().Instant = flag;
                    break;
                case "handat":
                    Sim().HandAtHead(h, new Vector3(x, y, z), yaw, pinch);
                    break;
                case "display": // --widget passthrough | immersive | toggle
                {
                    VrDisplay d = Need(mode).Session?.Display ?? throw new InvalidOperationException("no VR session");
                    string want = (widget ?? "toggle").ToLowerInvariant();
                    if (want == "toggle") d.Toggle();
                    else d.Set(want == "passthrough" ? DisplayMode.Passthrough : DisplayMode.Immersive, false);
                    break;
                }
                case "place": // --widget reset | lock | unlock | turn | size1   (--value = degrees for turn)
                {
                    VrPlacement p = Need(mode).Session?.Placement ?? throw new InvalidOperationException("no VR session");
                    switch ((widget ?? "reset").ToLowerInvariant())
                    {
                        case "reset": p.ResetInFront(); break;
                        case "lock": p.SetPlaceMode(false); break;
                        case "unlock": p.SetPlaceMode(true); break;
                        case "turn": p.TurnBy(value); break;
                        case "size1": p.ResetSize(); break;
                        default: throw new ArgumentException("place: reset | lock | unlock | turn | size1");
                    }

                    break;
                }
                case "hair": // --widget full | light | off
                    VrQuality.SetHair(VrSettings.ParseLevel(widget, HairLevel.Full), false);
                    break;
                case "tier":
                    VrQuality.Apply(Need(mode).Session != null && !Need(mode).Session.Input.Source.IsSimulated, flag ? VrQuality.Tier.Quest : VrQuality.Tier.Pc);
                    break;
                case "selftest":
                    VrScenarios.Start(widget);
                    return Json(VrScenarios.Report());
                case "result":
                    return Json(VrScenarios.Report());
                default:
                    throw new ArgumentException($"unknown action '{action}'");
            }

            return State();
        }

        static VrMode Need(VrMode m) => m != null ? m : throw new InvalidOperationException("no VR mode object (is this the viewer scene in Play mode?)");

        public static SimInputSource Sim()
        {
            VrRig r = VrRig.Instance;
            if (r == null || r.Sim == null) throw new InvalidOperationException("VR mode is not running on simulated hands (hm_vr --action enter --flag true)");
            return r.Sim;
        }

        public static VrWidget FindWidget(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("--widget is required");
            VrWidget w = VrPanel.All.Where(p => p.IsShown).SelectMany(p => p.Widgets).FirstOrDefault(x => x.Id == id && x.Kind != WidgetKind.Static);
            return w ?? throw new InvalidOperationException($"no visible widget '{id}' (hm_vr --action widgets)");
        }

        public static List<object> Widgets() => VrPanel.All.Where(p => p.IsShown).SelectMany(p => p.Widgets.Where(w => w.Kind != WidgetKind.Static)).Select(w =>
        {
            Vector3 c = w.WorldCentre;
            return (object)new Dictionary<string, object>
            {
                ["id"] = w.Id, ["panel"] = w.Panel.name, ["kind"] = w.Kind.ToString(), ["world"] = new[] { R(c.x), R(c.y), R(c.z) }, ["widthMm"] = R(w.RectMm.width), ["heightMm"] = R(w.RectMm.height),
                ["enabled"] = w.Enabled(), ["hovered"] = w.Hovered, ["pushed"] = w.Pushed, ["toggled"] = w.Toggled, ["live"] = w.Live, ["label"] = w.Label != null ? w.Label.text : null, ["clicks"] = w.ClickCount
            };
        }).ToList();

        static float R(float v) => Mathf.Round(v * 1000f) / 1000f;

        public static string State()
        {
            Dictionary<string, object> s = new()
            {
                ["vr"] = VrMode.Instance != null ? VrMode.Instance.State() : new Dictionary<string, object> { ["phase"] = "none" },
                ["library"] = new Newtonsoft.Json.Linq.JRaw(FilmLibraryCli.State())
            };
            return Json(s);
        }

        public static string Json(object o) => JsonConvert.SerializeObject(o, Formatting.None, new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
    }
}
