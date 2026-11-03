// T3 拥有：通用文本输入小对话框（新建文件夹 / 重命名）
using System;
using System.Drawing;
using System.Windows.Forms;
using Ctl = YijianNuodong.UI.Controls;

namespace YijianNuodong.UI
{
    public class InputDialog : Form
    {
        private readonly Label _titleLabel;
        private readonly Label _captionLabel;
        private readonly Ctl.BorderedPanel _inputHost;
        private readonly TextBox _inputBox;
        private readonly Label _errorLabel;
        private readonly Ctl.FlatButton _okButton;
        private readonly Ctl.FlatButton _cancelButton;

        /// <summary>输入结果</summary>
        public string Value { get; private set; }

        /// <summary>校验回调：返回错误提示文本，返回 null/空表示通过</summary>
        public Func<string, string> Validator { get; set; }

        public InputDialog() : this("请输入名称", string.Empty, string.Empty) { }

        public InputDialog(string title, string caption, string initialValue)
        {
            Value = initialValue == null ? string.Empty : initialValue;
            Text = string.IsNullOrEmpty(title) ? "请输入" : title;
            ClientSize = new Size(460, 236);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Surface;
            Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.Dpi;

            _titleLabel = new Label();
            _titleLabel.AutoSize = false;
            _titleLabel.Font = Theme.H2;
            _titleLabel.ForeColor = Theme.TextPrimary;
            _titleLabel.BackColor = Theme.Surface;
            _titleLabel.TextAlign = ContentAlignment.MiddleLeft;
            _titleLabel.Text = Text;
            Controls.Add(_titleLabel);

            _captionLabel = new Label();
            _captionLabel.AutoSize = false;
            _captionLabel.Font = Theme.Small;
            _captionLabel.ForeColor = Theme.TextSecondary;
            _captionLabel.BackColor = Theme.Surface;
            _captionLabel.TextAlign = ContentAlignment.TopLeft;
            _captionLabel.Text = caption == null ? string.Empty : caption;
            Controls.Add(_captionLabel);

            _inputHost = new Ctl.BorderedPanel();
            _inputHost.CornerRadius = 8;
            _inputHost.FillColor = Theme.Surface;
            _inputHost.BackdropColor = Theme.Surface;
            _inputHost.BorderColor = Theme.BorderStrong;
            Controls.Add(_inputHost);

            _inputBox = new TextBox();
            _inputBox.BorderStyle = BorderStyle.None;
            _inputBox.Font = Theme.Body;
            _inputBox.ForeColor = Theme.TextPrimary;
            _inputBox.BackColor = Theme.Surface;
            _inputBox.Text = Value;
            _inputBox.MaxLength = 120;
            _inputBox.KeyDown += InputBox_KeyDown;
            _inputHost.Controls.Add(_inputBox);

            _errorLabel = new Label();
            _errorLabel.AutoSize = false;
            _errorLabel.Font = Theme.Small;
            _errorLabel.ForeColor = Theme.Danger;
            _errorLabel.BackColor = Theme.Surface;
            _errorLabel.TextAlign = ContentAlignment.TopLeft;
            _errorLabel.Text = string.Empty;
            Controls.Add(_errorLabel);

            _okButton = new Ctl.FlatButton();
            _okButton.Text = "确定";
            _okButton.Style = Ctl.FlatButtonStyle.Primary;
            _okButton.Font = Theme.BodyBold;
            _okButton.Size = new Size(96, 36);
            _okButton.BackdropColor = Theme.Surface;
            _okButton.Click += OkButton_Click;
            Controls.Add(_okButton);

            _cancelButton = new Ctl.FlatButton();
            _cancelButton.Text = "取消";
            _cancelButton.Style = Ctl.FlatButtonStyle.Secondary;
            _cancelButton.Font = Theme.BodyBold;
            _cancelButton.Size = new Size(96, 36);
            _cancelButton.BackdropColor = Theme.Surface;
            _cancelButton.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_cancelButton);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;

            LayoutChildren();
            Resize += delegate { LayoutChildren(); };
            Shown += delegate
            {
                try
                {
                    _inputBox.Focus();
                    _inputBox.SelectionStart = 0;
                    _inputBox.SelectionLength = _inputBox.Text.Length;
                }
                catch { }
            };
        }

        private void LayoutChildren()
        {
            int width = ClientSize.Width;
            try
            {
                _titleLabel.SetBounds(24, 18, width - 48, 28);
                _captionLabel.SetBounds(24, 48, width - 48, 38);
                _inputHost.SetBounds(24, 92, width - 48, 40);
                _inputBox.SetBounds(12, 9, width - 48 - 24, 22);
                _errorLabel.SetBounds(26, 138, width - 52, 20);
                _cancelButton.Location = new Point(width - 24 - 96, 182);
                _okButton.Location = new Point(width - 24 - 96 - 10 - 96, 182);
            }
            catch { }
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                OkButton_Click(sender, EventArgs.Empty);
            }
        }

        private void OkButton_Click(object sender, EventArgs e)
        {
            string value = _inputBox.Text.Trim();
            if (value.Length == 0)
            {
                ShowError("名称不能为空。");
                return;
            }
            Func<string, string> validator = Validator;
            if (validator != null)
            {
                string error = null;
                try { error = validator(value); }
                catch (Exception ex) { error = "校验失败：" + ex.Message; }
                if (!string.IsNullOrEmpty(error))
                {
                    ShowError(error);
                    return;
                }
            }
            Value = value;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void ShowError(string message)
        {
            _errorLabel.Text = message;
            _errorLabel.ForeColor = Theme.Danger;
        }

        /// <summary>显示输入框，确定返回 true 并输出结果</summary>
        public static bool Prompt(IWin32Window owner, string title, string caption, string initialValue,
            Func<string, string> validator, out string value)
        {
            value = null;
            using (InputDialog dialog = new InputDialog(title, caption, initialValue))
            {
                dialog.Validator = validator;
                DialogResult result;
                try
                {
                    result = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                }
                catch (Exception ex)
                {
                    Core.Log.Warn("输入对话框显示失败: " + ex.Message);
                    return false;
                }
                if (result == DialogResult.OK)
                {
                    value = dialog.Value;
                    return true;
                }
                return false;
            }
        }
    }
}
