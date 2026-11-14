using System.Globalization;
using System.Text;

namespace ZendeskWatcher.Tracking;

/// <summary>
/// The small English language model of the Experimental features: a static embedding model (Model2Vec
/// "potion-base-8M" by MinishLab, MIT; 29,528 word pieces × 256 numbers, packed to 8-bit by Tools/pack-model.py).
/// A text's meaning is the average of its word pieces' vectors, so texts about the same thing ("site down", "website
/// not loading") end up close. Plain C#, runs on the PC, nothing is sent anywhere. Loaded only when a feature needs it.
/// </summary>
public sealed class Embeddings
{
    private readonly Dictionary<string, int> _vocab;
    private readonly float[] _scales;
    private readonly sbyte[] _values;
    private readonly int _unk;

    public int Dimensions { get; }

    private Embeddings(Dictionary<string, int> vocab, float[] scales, sbyte[] values, int dimensions)
    {
        _vocab = vocab;
        _scales = scales;
        _values = values;
        Dimensions = dimensions;
        _unk = vocab.GetValueOrDefault("[UNK]", -1);
    }

    private static Embeddings? _shared;
    private static bool _failed;
    private static readonly object Gate = new();

    /// <summary>The model, loaded on first use (about 8 MB in memory); null if it can't be read.</summary>
    public static Embeddings? Shared
    {
        get
        {
            lock (Gate)
            {
                if (_shared is not null || _failed)
                    return _shared;
                try
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    using var stream = typeof(Embeddings).Assembly.GetManifestResourceStream("embeddings.bin")
                        ?? throw new FileNotFoundException("embeddings.bin is not in the app");
                    _shared = Load(stream);
                    Log.Info($"Language model loaded ({_shared._vocab.Count} word pieces × {_shared.Dimensions}) in {clock.ElapsedMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    _failed = true;
                    Log.Error("Could not load the language model", ex);
                }
                return _shared;
            }
        }
    }

    /// <summary>Frees the model's memory when no feature uses it any more.</summary>
    public static void Unload()
    {
        lock (Gate)
            _shared = null;
    }

    public static Embeddings Load(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "ZWE1")
            throw new InvalidDataException("Not a packed embedding model");
        var rows = reader.ReadInt32();
        var dim = reader.ReadInt32();
        var vocab = new Dictionary<string, int>(rows, StringComparer.Ordinal);
        for (var i = 0; i < rows; i++)
            vocab[Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()))] = i;
        var scales = new float[rows];
        for (var i = 0; i < rows; i++)
            scales[i] = reader.ReadSingle();
        var bytes = reader.ReadBytes(rows * dim);
        if (bytes.Length != rows * dim)
            throw new InvalidDataException("The packed model is cut off");
        var values = new sbyte[bytes.Length];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return new Embeddings(vocab, scales, values, dim);
    }

    /// <summary>The text's meaning as a vector of length 1 (all zeros when no word is known).</summary>
    public float[] Embed(string? text)
    {
        var sum = new float[Dimensions];
        var count = 0;
        foreach (var id in TokenIds(text ?? ""))
        {
            var scale = _scales[id];
            var offset = id * Dimensions;
            for (var d = 0; d < Dimensions; d++)
                sum[d] += _values[offset + d] * scale;
            count++;
        }
        if (count == 0)
            return sum;
        var norm = MathF.Sqrt(sum.Sum(x => x * x));
        if (norm > 0)
            for (var d = 0; d < Dimensions; d++)
                sum[d] /= norm;
        return sum;
    }

    /// <summary>How alike two embedded texts are: about 0 (unrelated) to 1 (the same meaning).</summary>
    public static float Similarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0;
        float dot = 0;
        for (var i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return dot;
    }

    /// <summary>
    /// The word pieces, like the model's own (BERT, uncased) tokenizer: lower case, accents off, punctuation split
    /// off, then each word as the longest known pieces ("unpublished" → "un", "##published"). Unknown words are left out.
    /// </summary>
    internal IEnumerable<int> TokenIds(string text)
    {
        foreach (var word in Words(text))
        {
            if (word.Length > 100)
                continue;
            var pieces = new List<int>();
            var start = 0;
            while (start < word.Length)
            {
                var end = word.Length;
                var found = -1;
                while (end > start)
                {
                    var piece = start == 0 ? word[start..end] : "##" + word[start..end];
                    if (_vocab.TryGetValue(piece, out var id))
                    {
                        found = id;
                        break;
                    }
                    end--;
                }
                if (found < 0)
                {
                    pieces.Clear(); // like the tokenizer: the whole word becomes [UNK], which the model leaves out
                    break;
                }
                pieces.Add(found);
                start = end;
            }
            foreach (var id in pieces)
                if (id != _unk)
                    yield return id;
        }
    }

    /// <summary>Lower case without accents, split at spaces and punctuation (each punctuation mark is a word).</summary>
    internal static IEnumerable<string> Words(string text)
    {
        var normalized = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var current = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c == '\0' || c == '�')
                continue;
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
            }
            else if (IsPunctuation(c))
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                yield return c.ToString();
            }
            else
                current.Append(c);
        }
        if (current.Length > 0)
            yield return current.ToString();
    }

    private static bool IsPunctuation(char c) =>
        c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~' || char.IsPunctuation(c);
}
