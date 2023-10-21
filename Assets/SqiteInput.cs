using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Mono.Data.Sqlite;
using Shapes;
using Shapes.Lines;
using UnityEngine;

/// <summary>
/// Sqlite db needs to have a frames table
/// </summary>
public class SqliteInput : MonoBehaviour
{
    [Header("Input")] public string DbPath;

    static readonly string assetPath = Application.streamingAssetsPath;

    int FRAME_MAX = -1;
    int MAX_DANCERS = -1;

    enum Joints
    {
        Nose = 0,
        L_Eye = 1,
        R_Eye = 2,
        L_Ear = 3,
        R_Ear = 4,
        L_Shoulder = 5,
        R_Shoulder = 6,
        L_Elbow = 7,
        R_Elbow = 8,
        L_Wrist = 9,
        R_Wrist = 10,
        L_Hip = 11,
        R_Hip = 12,
        L_Knee = 13,
        R_Knee = 14,
        L_Ankle = 15,
        R_Ankle = 16
    }

    Dancer Lead;
    Dancer Follow;
    readonly List<StaticLink> AllLinks = new();

    void Start()
    {
        if (string.IsNullOrEmpty(DbPath))
        {
            DbPath = AssetPathFor("align.db");
            if (!File.Exists(DbPath))
            {
                return;
            }
        }

        Lead = ReadFrameFromDb(DbPath, "lead");
        Follow = ReadFrameFromDb(DbPath, "follow");

        // initiate dance skeletons
        for (int i = 0; i < 2; i++)
        {
            Dancer dancer = i == 0 ? Lead : Follow;

            for (int j = 0; j < 17; j++)
            {
                Polygon tetra = Instantiate(PolygonFactory.Instance.tetra);
                tetra.gameObject.SetActive(true);
                tetra.transform.SetParent(transform, false);
                tetra.transform.localScale = Vector3.one * .02f;
                dancer.Joints.Add(tetra);
            }
            
            AllLinks.Add(LinkFromTo((int)Joints.Nose, (int)Joints.L_Eye, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.Nose, (int)Joints.R_Eye, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Eye, (int)Joints.R_Eye, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Eye, (int)Joints.L_Ear, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Ear, (int)Joints.L_Shoulder, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Eye, (int)Joints.R_Ear, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Ear, (int)Joints.R_Shoulder, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Hip, (int)Joints.R_Knee, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Knee, (int)Joints.R_Ankle, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Hip, (int)Joints.L_Knee, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Knee, (int)Joints.L_Ankle, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Shoulder, (int)Joints.R_Elbow, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Elbow, (int)Joints.R_Wrist, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Shoulder, (int)Joints.L_Elbow, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Elbow, (int)Joints.L_Wrist, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Shoulder, (int)Joints.L_Shoulder, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Hip, (int)Joints.L_Hip, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Shoulder, (int)Joints.R_Hip, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Shoulder, (int)Joints.L_Hip, dancer));
        }

        StartCoroutine(Iterate(0));
    }

    StaticLink LinkFromTo(int index1, int index2, Dancer dancer)
    {
        StaticLink staticLink = Instantiate(StaticLink.prototypeStaticLink);
        staticLink.gameObject.SetActive(true);
        staticLink.SetColor(dancer.Role == Role.Lead ? Viridis.ViridisColor(0) : Viridis.ViridisColor(1));
        staticLink.transform.SetParent(transform, false);
        staticLink.LinkFromTo(dancer.Joints[index1].transform, dancer.Joints[index2].transform);
        return staticLink;
    }

    IEnumerator Iterate(int frameNumber)
    {
        if (frameNumber > FRAME_MAX)
        {
            frameNumber = 0;
        }

        Lead.SetPoseToFrame(frameNumber);
        Follow.SetPoseToFrame(frameNumber);

        foreach (StaticLink staticLink in AllLinks)
        {
            staticLink.UpdateLink();
        }

        yield return new WaitForSeconds(.03f);

        frameNumber++;
        StartCoroutine(Iterate(frameNumber));
    }

    Dancer ReadFrameFromDb(string dbPath, string role)
    {
        Dancer dancer = new(role == "lead" ? Role.Lead : Role.Follow);
        string connectionString = "URI=file:" + dbPath;

        using (IDbConnection conn = new SqliteConnection(connectionString))
        {
            conn.Open();

            List<string> columnNames = new List<string>
            {
                "id", "frame_id", "position_x", "position_y", "position_z"
            };

            using (IDbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = CommandString(columnNames, role);

                using (IDataReader reader = cmd.ExecuteReader())
                {
                    Dictionary<string, int> indexes = ColumnIndexes(reader, columnNames);
                    while (reader.Read())
                    {
                        int frameId = reader.GetInt32(indexes["frame_id"]);
                        if (frameId > FRAME_MAX)
                        {
                            FRAME_MAX = frameId;
                        }

                        Vector3 position = new(
                            reader.GetFloat(indexes["position_x"]),
                            reader.GetFloat(indexes["position_y"]),
                            reader.GetFloat(indexes["position_z"]));
                        

                        dancer.PosesByFrame.TryGetValue(frameId, out List<Vector3> pose);
                        if (pose == null)
                        {
                            pose = new List<Vector3>();
                            dancer.PosesByFrame.Add(frameId, pose);
                        }
                        pose.Add(position);
                    }
                }
            }
        }

        return dancer;
    }

    static string CommandString(IEnumerable<string> columnNames, string tableName)
    {
        string cmd = columnNames.Aggregate(
            "SELECT ",
            (current, columnName) => current + $"{columnName}, ");

        // remove last comma
        cmd = cmd.Substring(0, cmd.Length - 2) + " ";
        cmd += $"FROM {tableName}";

        return cmd;
    }

    static Dictionary<string, int> ColumnIndexes(IDataRecord reader, IEnumerable<string> columnNames)
    {
        return columnNames
            .ToDictionary(
                columnName => columnName,
                reader.GetOrdinal);
    }

    static string AssetPathFor(string asset)
    {
        return NormalizedPath(Path.Combine(assetPath, asset));
    }

    static string NormalizedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        Uri uri = new(path, UriKind.RelativeOrAbsolute);
        string returnUri = uri.IsAbsoluteUri
            ? uri.LocalPath
            : Path.GetFullPath(new Uri(Path.Combine(Application.dataPath, path)).AbsolutePath);
        returnUri = Uri.UnescapeDataString(returnUri);
        return returnUri;
    }
}