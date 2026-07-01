using nanoFramework.UI;
using System.Drawing;
using SpawnDev.UI;

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
    /// Built on the SpawnDev.UI widget library (WidgetScreen): a UISwitch arms pairing,
    /// a UILabel shows the code, a UIButton forgets the pairing. Being a WidgetScreen it
    /// also gets the navigator's slide transition + off-display capture. Back is the BOOT
    /// button (a WidgetScreen consumes taps so a stray tap can't accidentally leave).
    /// </summary>
    public class CompanionScreen : WidgetScreen
    {
        private readonly PairingService _pairing;
        private readonly UISwitch _pairSwitch;
        private readonly UILabel _codeLabel;
        private readonly UIButton _forgetButton;

        public CompanionScreen(Bitmap fb, int panelWidth, int panelHeight, PairingService pairing)
            : base(new WatchSurface(fb, panelWidth, panelHeight))
        {
            _pairing = pairing;
            var t = Theme.Current;

            var root = new UIPanel
            {
                X = 0, Y = 0, Width = panelWidth, Height = panelHeight, Background = t.Background,
            };

            root.Add(new UILabel
            {
                X = 0, Y = StatusBar.ReservedHeight + 8, Width = panelWidth, Height = 46,
                Text = "COMPANION", Scale = t.TitleScale, Center = true, Color = t.OnSurface,
            });

            // Controls stacked in the safe band: arm pairing, show the code, forget.
            var col = new UIColumn
            {
                X = SafeArea.EdgeInset, Y = StatusBar.ReservedHeight + 76,
                Width = panelWidth - 2 * SafeArea.EdgeInset, Height = 240, Spacing = t.Gap,
            };

            _pairSwitch = new UISwitch { Text = "PAIRING", Scale = t.BodyScale, Toggled = OnPairToggled };
            _codeLabel = new UILabel { Height = t.RowHeight, Text = "CODE  ------", Scale = t.BodyScale, Center = true, Color = t.Muted };
            _forgetButton = new UIButton
            {
                Height = t.RowHeight, Text = "FORGET", Scale = t.BodyScale,
                Background = t.Surface, Foreground = t.OnSurface, Clicked = OnForget,
            };
            col.Add(_pairSwitch);
            col.Add(_codeLabel);
            col.Add(_forgetButton);
            root.Add(col);

            // Hint + back note near the bottom (centered content never clips the round corners).
            root.Add(new UILabel
            {
                X = 0, Y = StatusBar.ReservedHeight + 76 + 240 + 10, Width = panelWidth, Height = 30,
                Text = "ENTER CODE IN COMPANION", Scale = t.SmallScale, Center = true, Color = t.Muted,
            });
            root.Add(new UILabel
            {
                X = 0, Y = panelHeight - 64, Width = panelWidth, Height = 30,
                Text = "BACK BUTTON TO EXIT", Scale = t.SmallScale, Center = true, Color = t.Muted,
            });

            Root = root;
        }

        public override void OnResume()
        {
            // Always start disarmed; the user must explicitly toggle pairing on.
            _pairSwitch.On = false;
            _codeLabel.Text = "CODE  ------";
            base.OnResume();
        }

        public override void OnPause()
        {
            // Leaving the page closes the pairing window - pairing is only armed while the user is
            // actually looking at this screen (TJ's design).
            if (_pairing != null) _pairing.EndPairingWindow();
            base.OnPause();
        }

        private void OnPairToggled(bool on)
        {
            if (_pairing == null) return;
            if (on)
            {
                string code = _pairing.BeginPairingWindow();
                _codeLabel.Text = "CODE  " + code;
                _codeLabel.Color = Theme.Current.OnSurface; // brighten the live code
            }
            else
            {
                _pairing.EndPairingWindow();
                _codeLabel.Text = "CODE  ------";
                _codeLabel.Color = Theme.Current.Muted;
            }
            Invalidate(); // repaint to show the updated code
        }

        // Watch-side "forget pairing": clears the paired peer + room so the watch reverts to its
        // unpaired/dev identity (the console + a fresh Companion can then re-pair). Mirrors the
        // Companion's "Forget Trust", but on the watch where the binding actually lives.
        private void OnForget()
        {
            if (_pairing == null) return;
            bool wasPaired = _pairing.IsPaired;
            _pairing.Unpair();
            _forgetButton.Text = wasPaired ? "FORGOTTEN" : "NOT PAIRED";
            Invalidate();
        }
    }
}
