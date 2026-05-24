using System.Numerics;

namespace PSVRPlayer.PSVR;

/// <summary>
/// Madgwick AHRS sensor fusion.
/// Matches the algorithm used in PSVRFramework's BMI055Integrator.
/// Input: gyro (rad/s), accel (any unit, will be normalised)
/// Output: orientation quaternion (world-relative)
/// </summary>
public class MadgwickFilter
{
    public float Beta { get; set; } = 0.1f;  // Filter gain – lower = smoother, higher = faster
    public float SampleRate { get; set; } = 1000f; // PSVR sends ~1000 reports/sec

    private float _q0 = 1f, _q1 = 0f, _q2 = 0f, _q3 = 0f;

    public void Reset()
    {
        _q0 = 1f; _q1 = 0f; _q2 = 0f; _q3 = 0f;
    }

    public void Update(float gx, float gy, float gz, float ax, float ay, float az)
    {
        float dt = 1f / SampleRate;

        // Normalise accelerometer
        float aNorm = MathF.Sqrt(ax * ax + ay * ay + az * az);
        if (aNorm == 0f) return;
        ax /= aNorm; ay /= aNorm; az /= aNorm;

        float q0 = _q0, q1 = _q1, q2 = _q2, q3 = _q3;

        // Gradient-descent objective function (gravity reference only)
        float f1 = 2f * (q1 * q3 - q0 * q2) - ax;
        float f2 = 2f * (q0 * q1 + q2 * q3) - ay;
        float f3 = 2f * (0.5f - q1 * q1 - q2 * q2) - az;

        // Jacobian transpose × f
        float j11 = -2f * q2, j12 = 2f * q3, j13 = -2f * q0, j14 = 2f * q1;
        float j21 = 2f * q1, j22 = 2f * q0, j23 = 2f * q3, j24 = 2f * q2;
        float j32 = -4f * q1, j33 = -4f * q2;

        float s0 = j13 * f2 + j14 * f1;  // (simplified, gravity only)
        float s1 = j11 * f1 + j21 * f2 + j32 * f3;
        float s2 = j12 * f1 + j22 * f2 + j33 * f3;
        float s3 = j14 * f1 + j23 * f2 + j24 * f2;

        // More precise version from the original paper
        s0 = -2f * q2 * f1 + 2f * q1 * f2;
        s0 += j13 * f2;

        // Full objective Jacobian × f (from Madgwick 2010 paper equations 25-28)
        s0 = -2f * q2 * f1 + 2f * q1 * f2;
        s1 = 2f * q3 * f1 + 2f * q0 * f2 - 4f * q1 * f3;
        s2 = -2f * q0 * f1 + 2f * q3 * f2 - 4f * q2 * f3;
        s3 = 2f * q1 * f1 + 2f * q2 * f2;

        float sNorm = MathF.Sqrt(s0 * s0 + s1 * s1 + s2 * s2 + s3 * s3);
        if (sNorm > 0f) { s0 /= sNorm; s1 /= sNorm; s2 /= sNorm; s3 /= sNorm; }

        // Rate of change from gyroscope
        float qDot0 = 0.5f * (-q1 * gx - q2 * gy - q3 * gz) - Beta * s0;
        float qDot1 = 0.5f * ( q0 * gx + q2 * gz - q3 * gy) - Beta * s1;
        float qDot2 = 0.5f * ( q0 * gy - q1 * gz + q3 * gx) - Beta * s2;
        float qDot3 = 0.5f * ( q0 * gz + q1 * gy - q2 * gx) - Beta * s3;

        q0 += qDot0 * dt;
        q1 += qDot1 * dt;
        q2 += qDot2 * dt;
        q3 += qDot3 * dt;

        float qNorm = MathF.Sqrt(q0 * q0 + q1 * q1 + q2 * q2 + q3 * q3);
        _q0 = q0 / qNorm; _q1 = q1 / qNorm; _q2 = q2 / qNorm; _q3 = q3 / qNorm;
    }

    /// <summary>Orientation as System.Numerics quaternion (x,y,z,w).</summary>
    public Quaternion Orientation => new Quaternion(_q1, _q2, _q3, _q0);
}
