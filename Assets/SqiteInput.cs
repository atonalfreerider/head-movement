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

    List<string> jointLabels = new()
    {
        "Head_top", "Thorax", "R_Shoulder", "R_Elbow", "R_Wrist", "L_Shoulder", "L_Elbow", "L_Wrist", "R_Hip", "R_Knee",
        "R_Ankle", "L_Hip", "L_Knee", "L_Ankle", "Pelvis", "Spine", "Head", "R_Hand", "L_Hand", "R_Toe", "L_Toe"
    };

    Dictionary<int, Dictionary<int, List<Vector3>>> DancersByFrame;
    readonly Dictionary<int, List<Polygon>> JointsByDancer = new();
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

        DancersByFrame = ReadFrameFromDb(DbPath);

        // initiate dance skeletons
        for (int i = 0; i <= MAX_DANCERS; i++)
        {
            List<Polygon> joints = new List<Polygon>();
            JointsByDancer.Add(i, joints);
            for (int j = 0; j < 21; j++)
            {
                Polygon tetra = Instantiate(PolygonFactory.Instance.tetra);
                tetra.gameObject.SetActive(true);
                tetra.transform.SetParent(transform, false);
                tetra.transform.localScale = Vector3.one * .2f;
                joints.Add(tetra);
            }


            AllLinks.Add(LinkFromTo(0, 16, joints));
            AllLinks.Add(LinkFromTo(16, 1, joints));
            AllLinks.Add(LinkFromTo(1, 15, joints));
            AllLinks.Add(LinkFromTo(15, 14, joints));
            AllLinks.Add(LinkFromTo(14, 8, joints));
            AllLinks.Add(LinkFromTo(14, 11, joints));
            AllLinks.Add(LinkFromTo(8, 9, joints));
            AllLinks.Add(LinkFromTo(9, 10, joints));
            AllLinks.Add(LinkFromTo(10, 19, joints));
            AllLinks.Add(LinkFromTo(11, 12, joints));
            AllLinks.Add(LinkFromTo(12, 13, joints));
            AllLinks.Add(LinkFromTo(13, 20, joints));
            AllLinks.Add(LinkFromTo(1, 2, joints));
            AllLinks.Add(LinkFromTo(2, 3, joints));
            AllLinks.Add(LinkFromTo(3, 4, joints));
            AllLinks.Add(LinkFromTo(4, 17, joints));
            AllLinks.Add(LinkFromTo(1, 5, joints));
            AllLinks.Add(LinkFromTo(5, 6, joints));
            AllLinks.Add(LinkFromTo(6, 7, joints));
            AllLinks.Add(LinkFromTo(7, 18, joints));
        }

        StartCoroutine(Iterate(0));
    }

    StaticLink LinkFromTo(int index1, int index2, IReadOnlyList<Polygon> joints)
    {
        StaticLink staticLink = Instantiate(StaticLink.prototypeStaticLink);
        staticLink.gameObject.SetActive(true);
        staticLink.transform.SetParent(transform, false);
        staticLink.from = joints[0].transform;
        staticLink.LinkFromTo(joints[index1].transform, joints[index2].transform);
        return staticLink;
    }

    IEnumerator Iterate(int frameNumber)
    {
        if (frameNumber > FRAME_MAX)
        {
            frameNumber = 0;
        }

        Dictionary<int, List<Vector3>> dancersInFrame = DancersByFrame[frameNumber];
        foreach ((int dancerId, List<Vector3> dancer) in dancersInFrame)
        {
            List<Polygon> joints = JointsByDancer[dancerId];
            for (int i = 0; i < dancer.Count; i++)
            {
                joints[i].transform.localPosition = dancer[i];
            }
        }

        foreach (StaticLink staticLink in AllLinks)
        {
            staticLink.UpdateLink();
        }

        yield return new WaitForSeconds(.03f);

        frameNumber++;
        StartCoroutine(Iterate(frameNumber));
    }

    Dictionary<int, Dictionary<int, List<Vector3>>> ReadFrameFromDb(string dbPath)
    {
        Dictionary<int, Dictionary<int, List<Vector3>>> dancersByFrame = new();
        string connectionString = "URI=file:" + dbPath;

        using (IDbConnection conn = new SqliteConnection(connectionString))
        {
            conn.Open();

            List<string> columnNames = new List<string>
            {
                "id", "frame_id", "dancer_id", "position_x", "position_y", "position_z"
            };

            using (IDbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = CommandString(columnNames, "frames");

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

                        int dancerId = reader.GetInt32(indexes["dancer_id"]);
                        if (dancerId > MAX_DANCERS)
                        {
                            MAX_DANCERS = frameId;
                        }

                        Vector3 position = new(
                            reader.GetFloat(indexes["position_x"]),
                            reader.GetFloat(indexes["position_y"]),
                            reader.GetFloat(indexes["position_z"]));

                        dancersByFrame.TryGetValue(frameId, out Dictionary<int, List<Vector3>> dancersInFrame);
                        if (dancersInFrame == null)
                        {
                            dancersInFrame = new();
                            dancersByFrame.Add(frameId, dancersInFrame);
                        }

                        dancersInFrame.TryGetValue(dancerId, out List<Vector3> dancer);
                        if (dancer == null)
                        {
                            dancer = new();
                            dancersInFrame.Add(dancerId, dancer);
                        }

                        dancer.Add(position);
                    }
                }
            }
        }

        return dancersByFrame;
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