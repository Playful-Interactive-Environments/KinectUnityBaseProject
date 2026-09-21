using UnityEngine;

public struct Hand
{
    public bool IsValid { get; private set; }
    public Vector3 Pointer { get; private set; }
    public Vector3 Center { get; private set; }
    public Vector3[] Points { get; private set; }

    public Hand(Vector3[] hullPoints, Vector3[] pathPoints)
    {
        this.Center = Extensions.AverageVector(hullPoints);
        this.Points = pathPoints;
        this.Pointer = FarthestPointFromCenter(Center, Points);
        this.IsValid = pathPoints.Length == 3 || pathPoints.Length == 6;
    }

    private static Vector3 FarthestPointFromCenter(Vector3 center, Vector3[] points)
    {
        var dist = 0.0f;
        var vect = Vector3.zero;

        foreach (var point in points)
        {
            var square = (center - point).sqrMagnitude;
            if (square > dist)
            {
                dist = square;
                vect = point;
            }
        }

        return vect;
    }
}

public struct Marker
{
    public byte Id { get; private set; }
    public bool Assign { get; private set; }
    public float Length { get; private set; }
    public Vector3 Center { get; private set; }
    public Vector3[] Points { get; private set; }
    public Quaternion Rotation { get; private set; }

    public Marker(byte id, Vector3 center, float length, Vector3[] points, Quaternion rotation, bool assign)
    {
        this.Id = id;
        this.Assign = assign;
        this.Center = center;
        this.Length = length;
        this.Points = points;
        this.Rotation = rotation;
    }
}