using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using ElintriaEngineC.Components;


namespace ElintriaEngineC.Components
{
    public class Transform  
    {
        public Vector3 Position = new Vector3(0.0f);
        public Quaternion Rotation = Quaternion.Identity;
        public Vector3 Scale = Vector3.One;


        public Transform() 
        {
            Vector3 Position = new Vector3(0.0f);
            Quaternion Rotation = Quaternion.Identity;
            Vector3 Scale = Vector3.One; 
        }











    }
}
