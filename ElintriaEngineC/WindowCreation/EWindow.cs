using OpenTK.Windowing.Desktop;
using System;
using System.Collections.Generic;
using System.Text;
using OpenTK.Mathematics;

namespace ElintriaEngineC.WindowCreation
{
    public class EWindow : GameWindow
    {
        private static int m_windowWidth;
        private static int m_windowHeight;
        protected Vector2 _currentMousePos;       // Updated every frame/move
        public static EWindow Instance { get; protected set; }


        public EWindow(int width, int height, string windowTitle) : base(GameWindowSettings.Default, NativeWindowSettings.Default)
        {


            m_windowWidth = width;
            m_windowHeight = height;
            this.CenterWindow(new Vector2i(m_windowWidth, m_windowHeight));
            this.Title = windowTitle;


        }


        public virtual Vector2 GetMousePos()
        {
            return _currentMousePos;
        }
    }
}
