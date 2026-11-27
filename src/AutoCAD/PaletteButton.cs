using System;
using System.Drawing;
using System.Windows.Forms;

namespace KotNode.AutoCAD
{
    // AutoCAD can stall before Button.Click when a docked WinForms palette is
    // restored from the host profile. Use a non-toggling checkbox button instead.
    // Report: https://forums.autodesk.com/t5/net-forum/docked-palette-set-buttons-cause-autocad-to-hang/td-p/11808618
    // Modal forms keep real Buttons (AcceptButton/CancelButton semantics).
    internal sealed class PaletteButton : CheckBox
    {
        bool tracing;
        public PaletteButton()
        {
            Appearance=Appearance.Button;
            AutoCheck=false;
            TextAlign=ContentAlignment.MiddleCenter;
            FlatStyle=FlatStyle.Standard;
            AccessibleRole=AccessibleRole.PushButton;
        }
        void BeginClickTrace()
        {
            if(tracing)return;
            tracing=true;
            Diagnostics.Begin("PALETTE");
            Diagnostics.EnableLiveTrace("PALETTE");
            Diagnostics.Step("Палитра: нажатие «"+Text+"»");
        }
        void EndClickTrace()
        {
            if(!tracing)return;
            Diagnostics.EndLiveTrace();tracing=false;
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if(e.Button==MouseButtons.Left)BeginClickTrace();
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            try{base.OnMouseUp(e);}
            finally{EndClickTrace();}
        }
        protected override void OnClick(EventArgs e)
        {
            BeginClickTrace();
            try
            {
                Diagnostics.Step("Палитра: обработчик Click");
                base.OnClick(e);
                Diagnostics.Step("Палитра: обработчик вернулся");
            }
            finally{EndClickTrace();}
        }
        protected override bool IsInputKey(Keys keyData)
            =>(keyData&Keys.KeyCode)==Keys.Enter||base.IsInputKey(keyData);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if(e.KeyCode==Keys.Enter)
            {
                if(Enabled)OnClick(EventArgs.Empty);
                e.Handled=true;e.SuppressKeyPress=true;
            }
            base.OnKeyDown(e);
        }
    }
}
