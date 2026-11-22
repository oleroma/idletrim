using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace IdleTrim
{
    // Edit the action and idle prompts, one per line.
    class PromptsDialog : DarkForm
    {
        internal readonly TextBox action = Box(), idle = Box();

        public List<string> Action { get { return Lines(action); } }
        public List<string> Idle { get { return Lines(idle); } }

        static TextBox Box()
        {
            return new TextBox
            {
                Multiline = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10f)
            };
        }

        static List<string> Lines(TextBox b)
        {
            return b.Text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
        }

        static Control Framed(TextBox b)
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(Theme.S(10), Theme.S(8), Theme.S(4), Theme.S(8)), Margin = new Padding(0, Theme.S(4), 0, Theme.S(12)) };
            p.Controls.Add(b);
            return p;
        }

        public PromptsDialog(PromptSet current, bool trained)
        {
            Text = "Prompts";
            ShowInTaskbar = false;
            ShowIcon = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(620), Theme.S(560));
            MinimumSize = new Size(Theme.S(480), Theme.S(440));

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(Theme.S(18)) };
            var intro = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(Theme.S(580), 0),
                ForeColor = Theme.Dim,
                Text = "SigLIP 2 scores each frame by how much more it looks like an action prompt than an idle prompt. " +
                       "Write one prompt per line, in English, describing what you see on screen." +
                       (trained ? "\n\nIdleTrim has learned from your edits, so it uses that instead of the prompts until you forget learning." : ""),
                Margin = new Padding(0, 0, 0, Theme.S(12))
            };
            action.Lines = current.Action.ToArray();
            idle.Lines = current.Idle.ToArray();

            var reset = new Btn("Built-in prompts");
            reset.Click += (o, e) =>
            {
                var b = PromptSet.BuiltIn();
                action.Lines = b.Action.ToArray();
                idle.Lines = b.Idle.ToArray();
            };
            var ok = new Btn("Save", true);
            var cancel = new Btn("Cancel") { DialogResult = DialogResult.Cancel };
            ok.Click += (o, e) =>
            {
                if (Action.Count == 0 || Idle.Count == 0)
                {
                    ThemedDialog.Ask(this, "Prompts", new string[,] { { null, "Write at least one action prompt and one idle prompt." } }, "OK", "Close");
                    return;
                }
                DialogResult = DialogResult.OK;
            };
            var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, Margin = new Padding(0) };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            reset.Anchor = AnchorStyles.Left;
            buttons.Controls.Add(reset, 0, 0);
            buttons.Controls.Add(cancel, 1, 0);
            buttons.Controls.Add(ok, 2, 0);

            grid.Controls.Add(intro, 0, 0);
            grid.Controls.Add(new Label { AutoSize = true, Text = "Action: keep frames like these", Font = new Font("Segoe UI Semibold", 9.5f) }, 0, 1);
            grid.Controls.Add(Framed(action), 0, 2);
            grid.Controls.Add(new Label { AutoSize = true, Text = "Idle: cut frames like these", Font = new Font("Segoe UI Semibold", 9.5f) }, 0, 3);
            grid.Controls.Add(Framed(idle), 0, 4);
            grid.Controls.Add(buttons, 0, 5);
            for (int i = 0; i < 6; i++) grid.RowStyles.Add(i == 2 || i == 4 ? new RowStyle(SizeType.Percent, 50) : new RowStyle(SizeType.AutoSize));
            grid.RowCount = 6;
            Controls.Add(grid);
            CancelButton = cancel;
        }
    }
}
