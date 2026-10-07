#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PortSteam;

// Minimal reflection-free protobuf codec (protobuf-net needs runtime codegen, which NativeAOT/iOS can't do).
public sealed class ProtoWriter
{
	readonly MemoryStream _ms = new();

	void Varint(ulong v)
	{
		while (v >= 0x80) { _ms.WriteByte((byte)(v | 0x80)); v >>= 7; }
		_ms.WriteByte((byte)v);
	}

	void Tag(int field, int wireType) => Varint((ulong)((field << 3) | wireType));

	public ProtoWriter U32(int f, uint v) { Tag(f, 0); Varint(v); return this; }
	public ProtoWriter U64(int f, ulong v) { Tag(f, 0); Varint(v); return this; }
	public ProtoWriter I32(int f, int v) { Tag(f, 0); Varint((ulong)(long)v); return this; }
	public ProtoWriter Bool(int f, bool v) { Tag(f, 0); Varint(v ? 1UL : 0UL); return this; }

	public ProtoWriter Fixed64(int f, ulong v)
	{
		Tag(f, 1);
		Span<byte> b = stackalloc byte[8];
		BinaryPrimitives.WriteUInt64LittleEndian(b, v);
		_ms.Write(b);
		return this;
	}

	public ProtoWriter Fixed32(int f, uint v)
	{
		Tag(f, 5);
		Span<byte> b = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32LittleEndian(b, v);
		_ms.Write(b);
		return this;
	}

	public ProtoWriter Bytes(int f, ReadOnlySpan<byte> v)
	{
		Tag(f, 2);
		Varint((ulong)v.Length);
		_ms.Write(v);
		return this;
	}

	public ProtoWriter Str(int f, string? v) => v == null ? this : Bytes(f, Encoding.UTF8.GetBytes(v));
	public ProtoWriter Msg(int f, ProtoWriter m) => Bytes(f, m.ToArray());
	public ProtoWriter Raw(ReadOnlySpan<byte> encodedFields) { _ms.Write(encodedFields); return this; }
	public byte[] ToArray() => _ms.ToArray();
}

public sealed class ProtoMsg
{
	// varint / fixed values are stored as ulong, length-delimited values as byte[].
	readonly Dictionary<int, List<object>> _fields = new();

	public static ProtoMsg Parse(ReadOnlySpan<byte> data)
	{
		var msg = new ProtoMsg();
		int pos = 0;
		while (pos < data.Length)
		{
			ulong key = ReadVarint(data, ref pos);
			int field = (int)(key >> 3), wt = (int)(key & 7);
			object value;
			switch (wt)
			{
				case 0: value = ReadVarint(data, ref pos); break;
				case 1: value = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(pos, 8)); pos += 8; break;
				case 2:
					int len = checked((int)ReadVarint(data, ref pos));
					value = data.Slice(pos, len).ToArray();
					pos += len;
					break;
				case 5: value = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4)); pos += 4; break;
				default: throw new InvalidDataException($"unsupported protobuf wire type {wt}");
			}
			if (!msg._fields.TryGetValue(field, out var list)) msg._fields[field] = list = new List<object>(1);
			list.Add(value);
		}
		return msg;
	}

	static ulong ReadVarint(ReadOnlySpan<byte> d, ref int pos)
	{
		ulong result = 0;
		for (int shift = 0; shift < 70; shift += 7)
		{
			byte b = d[pos++];
			result |= (ulong)(b & 0x7F) << shift;
			if (b < 0x80) return result;
		}
		throw new InvalidDataException("malformed varint");
	}

	object? Last(int f) => _fields.TryGetValue(f, out var l) && l.Count > 0 ? l[^1] : null;

	public bool Has(int f) => _fields.ContainsKey(f);
	public ulong U(int f, ulong def = 0) => Last(f) is ulong v ? v : def;
	public int I32(int f, int def = 0) => Last(f) is ulong v ? unchecked((int)v) : def;
	public bool B(int f) => U(f) != 0;
	public float F32(int f) => Last(f) is ulong v ? BitConverter.Int32BitsToSingle(unchecked((int)(uint)v)) : 0f;
	public byte[]? Bytes(int f) => Last(f) as byte[];
	public string? S(int f) => Last(f) is byte[] b ? Encoding.UTF8.GetString(b) : null;
	public ProtoMsg? M(int f) => Last(f) is byte[] b ? Parse(b) : null;

	public IEnumerable<ProtoMsg> Ms(int f)
	{
		if (!_fields.TryGetValue(f, out var l)) yield break;
		foreach (var o in l)
			if (o is byte[] b) yield return Parse(b);
	}
}
