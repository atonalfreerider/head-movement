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
        Head_top = 0,
        Thorax = 1,
        R_Shoulder = 2,
        R_Elbow = 3,
        R_Wrist = 4,
        L_Shoulder = 5,
        L_Elbow = 6,
        L_Wrist = 7,
        R_Hip = 8,
        R_Knee = 9,
        R_Ankle = 10,
        L_Hip = 11,
        L_Knee = 12,
        L_Ankle = 13,
        Pelvis = 14,
        Spine = 15,
        Head = 16,
        R_Hand = 17,
        L_Hand = 18,
        R_Toe = 19,
        L_Toe = 20
    }

    Dancer Lead;
    Dancer Follow;
    readonly List<StaticLink> AllLinks = new();

    void Start()
    {
        if (string.IsNullOrEmpty(DbPath))
        {
            DbPath = AssetPathFor("merge.db");
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

            for (int j = 0; j < 21; j++)
            {
                Polygon tetra = Instantiate(PolygonFactory.Instance.tetra);
                tetra.gameObject.SetActive(true);
                tetra.transform.SetParent(transform, false);
                tetra.transform.localScale = Vector3.one * .2f;
                dancer.Joints.Add(tetra);
            }
            
            AllLinks.Add(LinkFromTo((int)Joints.Head_top, (int)Joints.Head, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.Head, (int)Joints.Thorax, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.Thorax, (int)Joints.Spine, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.Spine, (int)Joints.Pelvis, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.Pelvis, (int)Joints.R_Hip, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.Pelvis, (int)Joints.L_Hip, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Hip, (int)Joints.R_Knee, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Knee, (int)Joints.R_Ankle, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Ankle, (int)Joints.R_Toe, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Hip, (int)Joints.L_Knee, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Knee, (int)Joints.L_Ankle, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Ankle, (int)Joints.L_Toe, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.Thorax, (int)Joints.R_Shoulder, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Shoulder, (int)Joints.R_Elbow, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Elbow, (int)Joints.R_Wrist, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Wrist, (int)Joints.R_Hand, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.Thorax, (int)Joints.L_Shoulder, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Shoulder, (int)Joints.L_Elbow, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Elbow, (int)Joints.L_Wrist, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Wrist, (int)Joints.L_Hand, dancer));
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