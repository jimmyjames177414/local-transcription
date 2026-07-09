namespace LocalTranscriber.Speakers;

public static class CosineSimilarity
{
    public static double Compute(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"Vector dimensions differ: {a.Length} vs {b.Length}.");
        }

        if (a.Length == 0)
        {
            return 0;
        }

        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    public static byte[] ToBlob(float[] vector)
    {
        byte[] blob = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);
        return blob;
    }

    public static float[] FromBlob(byte[] blob)
    {
        float[] vector = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, vector, 0, vector.Length * sizeof(float));
        return vector;
    }
}
