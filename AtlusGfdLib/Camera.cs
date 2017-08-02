﻿using System.Numerics;

namespace AtlusGfdLib
{
    public sealed class Camera
    {
        public Matrix4x4 Transform { get; set; }

        public float Field180 { get; set; }

        public float Field184 { get; set; }

        public float Field188 { get; set; }

        public float Field18C { get; set; }

        public float Field190 { get; set; }
    }
}