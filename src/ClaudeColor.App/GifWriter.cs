namespace ClaudeColor.App;

/// <summary>
/// Gravador mínimo de GIF animado (o GDI+ só salva um quadro): paleta global de até 255 cores + 1 transparente,
/// compressão LZW, repetição infinita (NETSCAPE2.0). Pixels com alfa &lt; 128 viram transparentes.
/// </summary>
internal static class GifWriter
{
    public static void Save(string path, IReadOnlyList<Bitmap> frames, int delayCentiseconds)
    {
        int w = frames[0].Width, h = frames[0].Height;
        var pixels = frames.Select(f => ReadArgb(f, w, h)).ToList();
        var palette = BuildPalette(pixels);
        var lookup = new Dictionary<int, byte>();

        using var fs = File.Create(path);
        using var o = new BinaryWriter(fs);
        o.Write("GIF89a"u8);
        o.Write((ushort)w); o.Write((ushort)h);
        o.Write((byte)0xF7); // tabela global, 8 bits, 256 entradas
        o.Write((byte)0); o.Write((byte)0);
        for (int i = 0; i < 256; i++)
        {
            int c = i < palette.Count ? palette[i] : 0;
            o.Write((byte)(c >> 16)); o.Write((byte)(c >> 8)); o.Write((byte)c);
        }
        // Repetir para sempre.
        o.Write(new byte[] { 0x21, 0xFF, 0x0B }); o.Write("NETSCAPE2.0"u8); o.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });

        foreach (var argb in pixels)
        {
            // Controle gráfico: descarta para o fundo, índice 0 transparente.
            o.Write(new byte[] { 0x21, 0xF9, 0x04, (2 << 2) | 1 }); o.Write((ushort)delayCentiseconds); o.Write(new byte[] { 0x00, 0x00 });
            o.Write((byte)0x2C); o.Write((ushort)0); o.Write((ushort)0); o.Write((ushort)w); o.Write((ushort)h); o.Write((byte)0);
            var indices = new byte[argb.Length];
            for (int i = 0; i < argb.Length; i++) indices[i] = Index(argb[i], palette, lookup);
            o.Write((byte)8); // tamanho mínimo do código LZW
            var data = Lzw(indices);
            for (int i = 0; i < data.Length; i += 255)
            {
                int n = Math.Min(255, data.Length - i);
                o.Write((byte)n);
                o.Write(data, i, n);
            }
            o.Write((byte)0);
        }
        o.Write((byte)0x3B);
    }

    private static int[] ReadArgb(Bitmap bmp, int w, int h)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var px = new int[w * h];
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, px, y * w, w);
            return px;
        }
        finally { bmp.UnlockBits(data); }
    }

    private static bool Transparent(int argb) => (argb >>> 24) < 128;

    /// <summary>Cor arredondada para 5 bits por canal (reduz os tons do antialias).</summary>
    private static int Round5(int argb) => argb & 0xF8F8F8;

    /// <summary>Índice 0 = transparente; depois as 255 cores mais frequentes.</summary>
    private static List<int> BuildPalette(List<int[]> frames)
    {
        var count = new Dictionary<int, int>();
        foreach (var f in frames)
            foreach (var p in f)
                if (!Transparent(p)) count[Round5(p)] = count.GetValueOrDefault(Round5(p)) + 1;
        var palette = new List<int> { 0 };
        palette.AddRange(count.OrderByDescending(kv => kv.Value).Take(255).Select(kv => kv.Key));
        return palette;
    }

    private static byte Index(int argb, List<int> palette, Dictionary<int, byte> lookup)
    {
        if (Transparent(argb)) return 0;
        int c = Round5(argb);
        if (lookup.TryGetValue(c, out var hit)) return hit;
        int best = 1, bestDist = int.MaxValue;
        for (int i = 1; i < palette.Count; i++)
        {
            int p = palette[i];
            int dr = ((c >> 16) & 255) - ((p >> 16) & 255), dg = ((c >> 8) & 255) - ((p >> 8) & 255), db = (c & 255) - (p & 255);
            int d = dr * dr + dg * dg + db * db;
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return lookup[c] = (byte)best;
    }

    /// <summary>LZW de GIF (códigos de 9 a 12 bits, "clear" quando a tabela enche).</summary>
    private static byte[] Lzw(byte[] indices)
    {
        const int ClearCode = 256, EndCode = 257, MaxCodes = 4096;
        var output = new List<byte>(indices.Length / 2);
        int size = 9, next = ClearCode + 2, acc = 0, bits = 0;
        bool resetSize = false;
        var dict = new Dictionary<int, int>();

        void Emit(int code)
        {
            acc |= code << bits;
            bits += size;
            while (bits >= 8) { output.Add((byte)acc); acc >>= 8; bits -= 8; }
            if (resetSize) { size = 9; resetSize = false; }
            else if (next > (1 << size) - 1 && size < 12) size++;
        }

        Emit(ClearCode);
        int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            int k = indices[i];
            int key = (prefix << 8) | k;
            if (dict.TryGetValue(key, out var code)) { prefix = code; continue; }
            Emit(prefix);
            prefix = k;
            if (next < MaxCodes) dict[key] = next++;
            else
            {
                dict.Clear();
                next = ClearCode + 2;
                resetSize = true;
                Emit(ClearCode);
            }
        }
        Emit(prefix);
        Emit(EndCode);
        if (bits > 0) output.Add((byte)acc);
        return output.ToArray();
    }
}
