namespace F10Recorder;

// Narrow construction eligibility, not a decoder. H.264 access-unit/picture
// syntax: ITU-T H.264 sections 7.3.2/7.3.3/7.4.1.2.4. The caller additionally
// requires one leading AUD and one base AVC VCL NAL per PES. Legal layouts
// outside this subset must use the existing exporter, never guessed offsets.
internal sealed class ReplayAvcPictureLayout
{
    int spsId = -1, ppsId = -1, frameBits, pocType, pocBits;
    int previousFrame = -1, previousPoc;
    bool bottomPoc;
    internal int Width { get; private set; }
    internal int Height { get; private set; }

    internal void Configuration(int type, byte[] nal)
    {
        if (nal.Length == 0 || (nal[0] & 128) != 0 || (nal[0] & 31) != type)
            throw new IOException("Replay AVC configuration NAL header is invalid.");
        var bits = new Bits(nal);
        if (type == 7)
        {
            if ((nal[0] & 0x60) == 0) throw new IOException("Replay AVC SPS is not a reference NAL.");
            int profile = bits.Read(8); int constraints = bits.Read(8); bits.Read(8);
            if ((constraints & 3) != 0) throw new IOException("Replay AVC SPS reserved bits are invalid.");
            spsId = bits.Ue();
            if (spsId > 31) throw new IOException("Invalid replay AVC SPS identity.");
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                if (bits.Ue() != 1 || bits.Ue() != 0 || bits.Ue() != 0 || bits.Read(1) != 0 || bits.Read(1) != 0)
                    throw new NotSupportedException("Replay AVC chroma, depth or scaling layout requires the existing exporter.");
            }
            else if (profile is not (66 or 77 or 88))
                throw new NotSupportedException("Replay AVC profile requires the existing exporter.");
            int frameMinus4 = bits.Ue();
            if (frameMinus4 > 12) throw new IOException("Invalid replay AVC frame-number width.");
            frameBits = frameMinus4 + 4; pocType = bits.Ue();
            if (pocType == 0)
            {
                int pocMinus4 = bits.Ue();
                if (pocMinus4 > 12) throw new IOException("Invalid replay AVC picture-order width.");
                pocBits = pocMinus4 + 4;
            }
            else if (pocType == 1) throw new NotSupportedException("Replay AVC picture-order layout requires the existing exporter.");
            else if (pocType != 2) throw new IOException("Invalid replay AVC picture-order type.");
            bits.Ue(); bits.Read(1); int widthMinus1 = bits.Ue(), heightMinus1 = bits.Ue();
            if (widthMinus1 > 4094 || heightMinus1 > 4094) throw new IOException("Replay AVC dimensions exceed their bound.");
            if (bits.Read(1) != 1) throw new NotSupportedException("Replay AVC field pictures require the existing exporter.");
            bits.Read(1);
            long cropX = 0, cropY = 0;
            if (bits.Read(1) != 0) { cropX = (long)bits.Ue() + bits.Ue(); cropY = (long)bits.Ue() + bits.Ue(); }
            // Eligibility above admits progressive 8-bit 4:2:0 only.
            long width = (widthMinus1 + 1L) * 16 - cropX * 2, height = (heightMinus1 + 1L) * 16 - cropY * 2;
            if (width is < 1 or > 65535 || height is < 1 or > 65535) throw new IOException("Replay AVC crop/dimensions are invalid.");
            Width = (int)width; Height = (int)height;
            bits.Read(1); // VUI follows; it cannot change progressive picture identity.
        }
        else
        {
            if ((nal[0] & 0x60) == 0) throw new IOException("Replay AVC PPS is not a reference NAL.");
            ppsId = bits.Ue(); int referencedSps = bits.Ue();
            if (ppsId > 255 || spsId < 0 || referencedSps != spsId)
                throw new IOException("Replay AVC PPS references an unavailable SPS.");
            bits.Read(1); bottomPoc = bits.Read(1) != 0;
            if (bits.Ue() != 0) throw new NotSupportedException("Replay AVC slice groups require the existing exporter.");
            bits.Ue(); bits.Ue(); bits.Read(1);
            if (bits.Read(2) == 3) throw new IOException("Replay AVC weighted biprediction flags are reserved.");
            bits.Ue(); bits.Ue(); bits.Ue(); bits.Read(1); bits.Read(1);
            if (bits.Read(1) != 0) throw new NotSupportedException("Redundant replay AVC pictures require the existing exporter.");
        }
    }

    internal void Picture(byte[] nal)
    {
        var bits = new Bits(nal, prefix: true);
        bool idr = (nal[0] & 31) == 5;
        if (idr && (nal[0] & 0x60) == 0) throw new IOException("Replay AVC IDR is not a reference NAL.");
        if ((nal[0] & 0x60) == 0) throw new NotSupportedException("Non-reference replay AVC pictures require the existing exporter.");
        if (bits.Ue() != 0) throw new NotSupportedException("Replay AVC PES does not begin at a primary picture.");
        int sliceType = bits.Ue();
        if (sliceType > 9) throw new IOException("Invalid replay AVC slice type.");
        if (idr && sliceType % 5 != 2) throw new IOException("Replay AVC IDR contains a non-intra slice.");
        if (sliceType % 5 is not (0 or 2)) throw new NotSupportedException("Replay AVC slice prediction requires the existing exporter.");
        if (ppsId < 0 || bits.Ue() != ppsId) throw new IOException("Replay AVC picture references an unavailable PPS.");
        int frameNumber = bits.Read(frameBits);
        if (idr && frameNumber != 0) throw new IOException("Replay AVC IDR frame number is invalid.");
        if (!idr && previousFrame < 0) throw new IOException("Replay AVC chunk starts without an independent picture.");
        if (!idr && frameNumber != (previousFrame + 1) % (1 << frameBits))
            throw new NotSupportedException("Replay AVC reference picture order requires the existing exporter.");
        if (idr) bits.Ue();
        if (pocType == 0)
        {
            int poc = bits.Read(pocBits);
            if (bottomPoc && bits.Ue() != 0) throw new NotSupportedException("Replay AVC field-order offsets require the existing exporter.");
            if (idr && poc != 0) throw new IOException("Replay AVC IDR picture-order count is invalid.");
            if (!idr && poc != (previousPoc + 2) % (1 << pocBits))
                throw new NotSupportedException("Replay AVC presentation reordering requires the existing exporter.");
            previousPoc = poc;
        }
        // Type2 reference pictures derive their progressive POC from the proven
        // increasing frame_num. Type0 additionally requires successive even POC.
        previousFrame = frameNumber;
    }

    sealed class Bits
    {
        readonly byte[] bytes;
        int position;
        internal Bits(byte[] nal, bool prefix = false)
        {
            var rbsp = new List<byte>(nal.Length); int zeros = 0;
            for (int n = 1; n < nal.Length; n++)
            {
                byte value = nal[n];
                if (zeros >= 2 && value == 3)
                {
                    if (n + 1 >= nal.Length && prefix) break;
                    if (n + 1 >= nal.Length || nal[n + 1] > 3) throw new IOException("Invalid replay AVC emulation prevention.");
                    zeros = 0; continue;
                }
                rbsp.Add(value); zeros = value == 0 ? zeros + 1 : 0;
            }
            bytes = rbsp.ToArray();
        }
        internal int Read(int count)
        {
            if (count < 0 || count > 30 || bytes.Length * 8 - position < count)
                throw new IOException("Replay AVC picture/configuration header is truncated.");
            int value = 0;
            for (int n = 0; n < count; n++, position++) value = (value << 1) | (bytes[position / 8] >> (7 - position % 8) & 1);
            return value;
        }
        internal int Ue()
        {
            int zeros = 0;
            while (Read(1) == 0) if (++zeros > 29) throw new IOException("Replay AVC header number exceeds its bound.");
            return ((1 << zeros) - 1) + Read(zeros);
        }
    }
}
