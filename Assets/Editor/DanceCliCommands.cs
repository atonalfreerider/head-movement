using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Unity.Pipeline.Commands;
using UnityEditor;

/// <summary>
/// Unity CLI surface for the VIEWER_SPEC v3 dance layers (Assets/Tour, Assets/DanceGraph, Assets/Overlays). Play mode.
///
///   unity command hm_tour --action start            # Orbit -> Overhead -> Geometry -> Physics -> Dance graph -> Fingerprint
///   unity command hm_tour --action goto --state overhead
///   unity command hm_tour --action status | next | stop
///   unity command hm_graph --mode path | fingerprint | static | off
///   unity command hm_layer --layer counterbalance|traces|graph --visible true   (HeadMovement routes these here)
/// </summary>
public static class DanceCliCommands
{
    static DanceLayers Require()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("enter Play mode first (unity command editor_play)");
        DanceLayers layers = DanceLayers.Ensure();
        if (layers == null) throw new InvalidOperationException("no HeadMovement in the open scene (open Assets/head-movement.unity)");
        return layers;
    }

    [CliCommand("hm_tour", "Directed tour (VIEWER_SPEC 6): start | stop | status | next | goto --state orbit|overhead|geometry|physics|dance_graph|fingerprint")]
    public static string Tour(
        [CliArg("action", "start|stop|status|next|goto")] string action = "status",
        [CliArg("state", "view state for goto, or the first state for start")] string state = null,
        [CliArg("measures", "measures per state for start (default 2; the dance graph state takes 3)")] int measures = 0,
        [CliArg("restart", "start: restart the take at measure 1 (default true)")] bool restart = true)
    {
        DanceLayers layers = Require();
        DanceTour tour = layers.Tour;
        switch ((action ?? "status").ToLowerInvariant())
        {
            case "start": return tour.StartTour(measures, state, restart);
            case "stop":
                tour.Stop();
                return tour.Status();
            case "next": return tour.Next();
            case "goto":
            case "hold":
                return tour.Hold(DanceTour.Parse(state));
            case "status": return tour.Status();
            default: throw new ArgumentException($"unknown action '{action}' (start|stop|status|next|goto)");
        }
    }

    [CliCommand("hm_graph", "Dance graph (VIEWER_SPEC 3.10/3.11): path (miniature couple + chase camera) | fingerprint (dwell heat map) | static (graph in the room) | off")]
    public static string Graph([CliArg("mode", "path|fingerprint|static|off")] string mode)
    {
        DanceLayers layers = Require();
        DanceTour tour = layers.Tour;
        switch ((mode ?? "").ToLowerInvariant())
        {
            case "path":
                RequireGraph(layers);
                tour.Hold(DanceTour.View.DanceGraph);
                break;
            case "fingerprint":
                RequireGraph(layers);
                tour.Hold(DanceTour.View.Fingerprint);
                break;
            case "static":
                RequireGraph(layers);
                if (tour.Current is DanceTour.View.DanceGraph or DanceTour.View.Fingerprint) tour.Stop();
                DanceLayers.SetLayer("graph", true);
                break;
            case "off":
                if (tour.Current is DanceTour.View.DanceGraph or DanceTour.View.Fingerprint) tour.Stop();
                DanceLayers.SetLayer("graph", false);
                break;
            default: throw new ArgumentException($"unknown mode '{mode}' (path|fingerprint|static|off)");
        }

        layers.Refresh();
        return JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["graph"] = layers.Graph?.State(), ["tour"] = tour.State()
        });
    }

    static void RequireGraph(DanceLayers layers)
    {
        if (layers.Graph == null) throw new InvalidOperationException("this capture has no moves/graph.json (run python -m dancecap.moves run <take>)");
    }
}
