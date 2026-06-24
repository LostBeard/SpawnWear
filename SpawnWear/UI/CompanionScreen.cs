using nanoFramework.UI;
using System.Drawing;

namespace SpawnWear.UI
{
    /// <summary>
    /// Settings &gt; Companion sub-page (pushed onto the navigator). Pairing is armed
    /// ONLY while this page is open and the toggle is ON: toggling on opens a pairing
    /// window and shows a 6-digit code the user types into the Blazor Companion. The
    /// code binds the Ed25519 key exchange to physical presence (a MITM that hasn't
    /// seen the watch screen can't complete pairing). Leaving the page (OnPause) or
    /// toggling off closes the window immediately.
    ///
    /// Phase 2 delivers the UI + the windowed lifecycle; Phase 3 wires the actual
    /// code-authenticated handshake into <see cref="PairingService"/>.
    /// </summary>
    public class CompanionScreen : IScreen
    {
        private readonly Bitmap _fb;
        private readonly int _panelWidth;
        private readonly int _panelHeight;
        private readonly PairingService _pairing;
        private readonly ListView _list;
        private readonly ListView.Row _pairRow;
        private readonly ListView.Row _codeRow;
        private readonly ListView.Row _forgetRow;
        private bool _pairingOn;
        private StatusBar _statusBar;
        public void SetStatusBar(StatusBar bar) { _statusBar = bar; }

        public CompanionScreen(Bitmap fb, int panelWidth, int panelHeight, PairingService pairing)
        {
            _fb = fb;
            _panelWidth = panelWidth;
            _panelHeight = panelHeight;
            _pairing = pairing;

            int rowHeight = 54;
            int listWidth = panelWidth - 40;
            int listX = (panelWidth - listWidth) / 2;
            // Two rows just below the title, well inside the safe band (clear of the
            // ~100px rounded corners).
            int listY = StatusBar.ReservedHeight + 16 + SmallFont.GlyphHeight * 4 + 16;

            _pairRow = new ListView.Row { Label = "PAIRING", Value = "OFF", OnTap = TogglePairing };
            _codeRow = new ListView.Row { Label = "CODE", Value = "------", OnTap = null };
            _forgetRow = new ListView.Row { Label = "FORGET", Value = "-", OnTap = ForgetPairing };
            _list = new ListView(_fb, listX, listY, listWidth, rowHeight, 4,
                new ListView.Row[] { _pairRow, _codeRow, _forgetRow });
        }

        public void Tick()
        {
            _statusBar?.Render(false);
            _list.Tick();
        }

        public void Invalidate()
        {
            _fb.Clear();
            _fb.FillRectangle(0, 0, _panelWidth, _panelHeight, Color.Black);

            int statusBarHeight = _statusBar != null ? StatusBar.ReservedHeight : 0;
            const string title = "COMPANION";
            int titleScale = 4;
            int titleWidth = SmallFont.MeasureString(title, titleScale);
            SmallFont.DrawString(_fb, title, (_panelWidth - titleWidth) / 2, statusBarHeight + 16, titleScale, Color.White);

            // Instruction line (centered -> always inside the round screen).
            const string hint = "ENTER CODE IN COMPANION";
            int hintScale = 2;
            int hintWidth = SmallFont.MeasureString(hint, hintScale);
            SmallFont.DrawString(_fb, hint, (_panelWidth - hintWidth) / 2, _panelHeight / 2 + 40, hintScale, Color.White);

            const string footer = "TAP OUTSIDE TO BACK";
            int footerScale = 2;
            int footerWidth = SmallFont.MeasureString(footer, footerScale);
            SmallFont.DrawString(_fb, footer, (_panelWidth - footerWidth) / 2, _panelHeight - 60, footerScale, Color.White);

            // Explicit full-panel flush: this page has a large empty region below
            // the rows that must overwrite whatever screen was underneath (the
            // sub-page is pushed over Settings), so don't rely on a default flush.
            _fb.Flush(0, 0, _panelWidth, _panelHeight);
            _statusBar?.Render(true);
            _list.Invalidate();
        }

        public void OnResume()
        {
            // Always start disarmed; the user must explicitly toggle pairing on.
            _pairingOn = false;
            _pairRow.Value = "OFF";
            _codeRow.Value = "------";
            _forgetRow.Value = _pairing != null && _pairing.IsPaired ? "TAP" : "-"; // TAP to unpair if paired
            Invalidate();
        }

        public void OnPause()
        {
            // Leaving the page closes the pairing window - pairing is only armed
            // while the user is actually looking at this screen (TJ's design).
            _pairingOn = false;
            if (_pairing != null) _pairing.EndPairingWindow();
        }

        public bool OnTap(int x, int y) => _list.HandleTap(x, y);

        private void TogglePairing()
        {
            if (_pairing == null) return;
            _pairingOn = !_pairingOn;
            if (_pairingOn)
            {
                string code = _pairing.BeginPairingWindow();
                _pairRow.Value = "ON";
                _codeRow.Value = code;
            }
            else
            {
                _pairing.EndPairingWindow();
                _pairRow.Value = "OFF";
                _codeRow.Value = "------";
            }
        }

        // Watch-side "forget pairing": clears the paired peer + room so the watch reverts to its
        // unpaired/dev identity (the console + a fresh Companion can then re-pair). Mirrors the
        // Companion's "Forget Trust", but on the watch where the binding actually lives.
        private void ForgetPairing()
        {
            if (_pairing == null) return;
            bool wasPaired = _pairing.IsPaired;
            _pairing.Unpair();
            _forgetRow.Value = wasPaired ? "DONE" : "NONE";
        }
    }
}
