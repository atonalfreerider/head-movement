using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Minimal PLY reader for 3D Gaussian Splatting exports (x,y,z, f_dc_0..2, opacity, scale_0..2, rot_*) and plain
/// coloured point clouds (x,y,z, red,green,blue). Thread-safe: no Unity API calls except math structs.
/// </summary>
public static class SplatPly
{
    const float ShC0 = 0.28209479177387814f;

    public class Data
    {
        public Vector3[] Positions;
        public Color[] Colors; // linear-ish rgb + alpha
        public float[] Radii; // metres, ~3 sigma of the largest axis
    }

    public static Data Load(string path, Matrix4x4 sceneToUnity, int maxSplats, float minAlpha = 0.04f)
    {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);

        (string format, int count, List<(string name, string type)> props) = ReadHeader(reader);
        if (format != "binary_little_endian")
        {
            throw new NotSupportedException($"{path}: PLY format {format} (only binary_little_endian)");
        }

        Dictionary<string, (int offset, string type)> fields = new();
        int stride = 0;
        foreach ((string name, string type) in props)
        {
            fields[name] = (stride, type);
            stride += SizeOf(type);
        }

        bool gaussian = fields.ContainsKey("f_dc_0") && fields.ContainsKey("opacity");
        bool rgb = fields.ContainsKey("red");
        bool scaled = fields.ContainsKey("scale_0");
        int step = Mathf.Max(1, Mathf.CeilToInt(count / (float)Mathf.Max(1, maxSplats)));
        float uniformScale = Mathf.Pow(Mathf.Abs(sceneToUnity.determinant), 1f / 3f);

        (int, string) Field(string name) => fields.TryGetValue(name, out (int, string) f) ? f : (-1, null);
        (int, string) fx = Field("x"), fy = Field("y"), fz = Field("z");
        (int, string) fOpacity = Field("opacity"), fDc0 = Field("f_dc_0"), fDc1 = Field("f_dc_1"), fDc2 = Field("f_dc_2");
        (int, string) fS0 = Field("scale_0"), fS1 = Field("scale_1"), fS2 = Field("scale_2");
        (int, string) fR = Field("red"), fG = Field("green"), fB = Field("blue");

        List<Vector3> positions = new(count / step + 1);
        List<Color> colors = new(count / step + 1);
        List<float> radii = new(count / step + 1);
        byte[] row = new byte[stride];

        for (int i = 0; i < count; i++)
        {
            if (reader.Read(row, 0, stride) != stride) break;
            if (i % step != 0) continue;

            float F((int offset, string type) f) => ReadFloat(row, f.offset, f.type);

            Vector3 p = new(F(fx), F(fy), F(fz));
            Color c;
            float radius;
            if (gaussian)
            {
                float alpha = 1f / (1f + Mathf.Exp(-F(fOpacity)));
                if (alpha < minAlpha) continue;

                c = new Color(
                    Mathf.Clamp01(0.5f + ShC0 * F(fDc0)),
                    Mathf.Clamp01(0.5f + ShC0 * F(fDc1)),
                    Mathf.Clamp01(0.5f + ShC0 * F(fDc2)),
                    alpha);
                float s = scaled ? Mathf.Max(F(fS0), Mathf.Max(F(fS1), F(fS2))) : Mathf.Log(0.01f);
                // denser subsampling -> larger discs so the surface stays closed
                radius = 3f * Mathf.Exp(s) * Mathf.Sqrt(step);
            }
            else
            {
                c = rgb ? new Color(F(fR) / 255f, F(fG) / 255f, F(fB) / 255f, 1f) : Color.white;
                radius = 0.01f * Mathf.Sqrt(step);
            }

            positions.Add(sceneToUnity.MultiplyPoint3x4(p));
            colors.Add(c);
            radii.Add(Mathf.Min(radius * uniformScale, 0.5f));
        }

        return new Data { Positions = positions.ToArray(), Colors = colors.ToArray(), Radii = radii.ToArray() };
    }

    static (string format, int count, List<(string, string)> props) ReadHeader(BinaryReader reader)
    {
        string format = null;
        int count = 0;
        bool inVertex = false;
        List<(string, string)> props = new();
        while (true)
        {
            string line = ReadLine(reader).Trim();
            if (line == "end_header") break;

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            switch (parts[0])
            {
                case "format":
                    format = parts[1];
                    break;
                case "element":
                    inVertex = parts[1] == "vertex";
                    if (inVertex) count = int.Parse(parts[2]);
                    break;
                case "property" when inVertex:
                    if (parts[1] == "list") throw new NotSupportedException("list properties in vertex element");
                    props.Add((parts[2], parts[1]));
                    break;
            }
        }

        return (format, count, props);
    }

    static string ReadLine(BinaryReader reader)
    {
        StringBuilder sb = new();
        while (true)
        {
            char ch = (char)reader.ReadByte();
            if (ch == '\n') return sb.ToString();
            sb.Append(ch);
        }
    }

    static int SizeOf(string type) => type switch
    {
        "float" or "float32" or "int" or "int32" or "uint" or "uint32" => 4,
        "double" or "float64" => 8,
        "uchar" or "uint8" or "char" or "int8" => 1,
        "short" or "int16" or "ushort" or "uint16" => 2,
        _ => throw new NotSupportedException($"PLY type {type}")
    };

    static float ReadFloat(byte[] row, int offset, string type) => type switch
    {
        "float" or "float32" => BitConverter.ToSingle(row, offset),
        "double" or "float64" => (float)BitConverter.ToDouble(row, offset),
        "uchar" or "uint8" => row[offset],
        "char" or "int8" => (sbyte)row[offset],
        "short" or "int16" => BitConverter.ToInt16(row, offset),
        "ushort" or "uint16" => BitConverter.ToUInt16(row, offset),
        "int" or "int32" => BitConverter.ToInt32(row, offset),
        "uint" or "uint32" => BitConverter.ToUInt32(row, offset),
        _ => 0
    };
}
