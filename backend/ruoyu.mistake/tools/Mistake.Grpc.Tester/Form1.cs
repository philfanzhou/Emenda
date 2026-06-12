using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using Grpc.Net.Client;
using Google.Protobuf;
using Ruoyu.Study.Mistake.Contract.Protos;

namespace Mistake.Grpc.Tester;

public class Form1 : Form
{
    private MistakeGrpcService.MistakeGrpcServiceClient _client = null!;
    private string[] _selectedImagePaths = Array.Empty<string>();

    // UI Controls
    private TextBox txtUrl = null!;
    private TextBox txtStudentId = null!;
    private RichTextBox txtLog = null!;
    private TabControl tabControl = null!;
    
    // Upload Tab
    private ComboBox cbSubject = null!;
    private ComboBox cbGrade = null!;
    private TextBox txtContent = null!;
    private TextBox txtCorrectAnswer = null!;
    private TextBox txtStudentAnswer = null!;
    private Label lblImages = null!;

    // List Tab
    private DataGridView dgvMistakes = null!;

    // Actions Tab
    private TextBox txtMistakeId = null!;

    public Form1()
    {
        InitializeUI();
        InitializeGrpcClient();
    }

    private void InitializeGrpcClient()
    {
        var handler = new System.Net.Http.HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = 
            System.Net.Http.HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        var channel = global::Grpc.Net.Client.GrpcChannel.ForAddress(txtUrl.Text, new GrpcChannelOptions { HttpHandler = handler });
        _client = new MistakeGrpcService.MistakeGrpcServiceClient(channel);
        Log("gRPC Client connected to: " + txtUrl.Text);
    }

    private void InitializeUI()
    {
        this.Text = "错题本管理系统 - 测试客户端 (Human Friendly)";
        this.Size = new Size(1000, 750);
        this.StartPosition = FormStartPosition.CenterScreen;

        // --- Top Panel (Config) ---
        var pnlTop = new Panel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(10) };
        pnlTop.Controls.Add(new Label { Text = "Server URL:", Location = new Point(10, 15), AutoSize = true });
        txtUrl = new TextBox { Text = "https://localhost:7120", Location = new Point(90, 12), Width = 200 };
        pnlTop.Controls.Add(txtUrl);
        
        pnlTop.Controls.Add(new Label { Text = "Student ID:", Location = new Point(310, 15), AutoSize = true });
        txtStudentId = new TextBox { Text = "student-001", Location = new Point(380, 12), Width = 150 };
        pnlTop.Controls.Add(txtStudentId);
        
        var btnConnect = new Button { Text = "刷新连接 (Update Connection)", Location = new Point(550, 10), Width = 200 };
        btnConnect.Click += (s, e) => InitializeGrpcClient();
        pnlTop.Controls.Add(btnConnect);

        this.Controls.Add(pnlTop);

        // --- Bottom Panel (Logs) ---
        var pnlBottom = new GroupBox { Text = "运行日志 (Logs)", Dock = DockStyle.Bottom, Height = 180 };
        txtLog = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.Black, ForeColor = Color.LightGray, Font = new Font("Consolas", 10) };
        pnlBottom.Controls.Add(txtLog);
        this.Controls.Add(pnlBottom);

        // --- Center (Tabs) ---
        tabControl = new TabControl { Dock = DockStyle.Fill, Padding = new Point(10, 5) };
        
        var tabUpload = new TabPage("📝 上传错题 (Upload Mistake)");
        SetupUploadTab(tabUpload);
        tabControl.TabPages.Add(tabUpload);

        var tabList = new TabPage("📋 错题列表 (Mistake List)");
        SetupListTab(tabList);
        tabControl.TabPages.Add(tabList);

        var tabActions = new TabPage("⚙️ 错题操作与分析 (Actions & AI)");
        SetupActionsTab(tabActions);
        tabControl.TabPages.Add(tabActions);

        this.Controls.Add(tabControl);
        this.Controls.SetChildIndex(pnlTop, 0);
        this.Controls.SetChildIndex(tabControl, 1);
        this.Controls.SetChildIndex(pnlBottom, 2);
    }

    private void SetupUploadTab(TabPage tab)
    {
        tab.AutoScroll = true;
        int y = 20;

        tab.Controls.Add(new Label { Text = "科目 (Subject):", Location = new Point(20, y), AutoSize = true });
        cbSubject = new ComboBox { Location = new Point(120, y), Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
        cbSubject.Items.AddRange(new[] { "语文", "数学", "英语", "物理", "化学" });
        cbSubject.SelectedIndex = 1; // Default to Math
        tab.Controls.Add(cbSubject);

        tab.Controls.Add(new Label { Text = "年级 (Grade):", Location = new Point(260, y), AutoSize = true });
        cbGrade = new ComboBox { Location = new Point(350, y), Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
        cbGrade.Items.AddRange(new[] { "一年级", "二年级", "三年级", "四年级", "五年级", "六年级", "初一", "初二", "初三", "高一", "高二", "高三" });
        cbGrade.SelectedIndex = 8; // Default to 初三
        tab.Controls.Add(cbGrade);

        y += 50;
        tab.Controls.Add(new Label { Text = "题目内容:\r\n(Content)", Location = new Point(20, y), AutoSize = true });
        txtContent = new TextBox { Location = new Point(120, y), Width = 750, Height = 80, Multiline = true, ScrollBars = ScrollBars.Vertical, Text = "已知函数 f(x) = ax^2 + bx + c..." };
        tab.Controls.Add(txtContent);

        y += 100;
        tab.Controls.Add(new Label { Text = "正确答案:\r\n(Correct Ans)", Location = new Point(20, y), AutoSize = true });
        txtCorrectAnswer = new TextBox { Location = new Point(120, y), Width = 750, Height = 60, Multiline = true, ScrollBars = ScrollBars.Vertical, Text = "x = 1, x = -1" };
        tab.Controls.Add(txtCorrectAnswer);

        y += 80;
        tab.Controls.Add(new Label { Text = "学生答案:\r\n(Student Ans)", Location = new Point(20, y), AutoSize = true });
        txtStudentAnswer = new TextBox { Location = new Point(120, y), Width = 750, Height = 60, Multiline = true, ScrollBars = ScrollBars.Vertical, Text = "x = 2" };
        tab.Controls.Add(txtStudentAnswer);

        y += 80;
        tab.Controls.Add(new Label { Text = "错题原图:\r\n(Images)", Location = new Point(20, y), AutoSize = true });
        var btnSelectImages = new Button { Text = "选择本地图片 (Browse...)", Location = new Point(120, y), Width = 180, Height = 30 };
        lblImages = new Label { Text = "未选择任何图片 (0 images)", Location = new Point(320, y + 8), AutoSize = true, ForeColor = Color.Blue };
        btnSelectImages.Click += (s, e) => {
            using var ofd = new OpenFileDialog { Multiselect = true, Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp" };
            if (ofd.ShowDialog() == DialogResult.OK) {
                _selectedImagePaths = ofd.FileNames;
                lblImages.Text = $"已选择 {_selectedImagePaths.Length} 张图片";
            }
        };
        tab.Controls.Add(btnSelectImages);
        tab.Controls.Add(lblImages);

        y += 60;
        var btnUpload = new Button { Text = "🚀 一键上传错题 (Submit)", Location = new Point(120, y), Width = 250, Height = 45, BackColor = Color.LightGreen, Font = new Font(this.Font, FontStyle.Bold) };
        btnUpload.Click += async (s, e) => {
            try {
                btnUpload.Enabled = false;
                var req = new UploadMistakeRequest {
                    StudentId = txtStudentId.Text,
                    Subject = cbSubject.SelectedIndex + 1,
                    Grade = cbGrade.SelectedIndex + 1,
                    Content = txtContent.Text,
                    CorrectAnswer = txtCorrectAnswer.Text,
                    StudentAnswer = txtStudentAnswer.Text,
                    Comments = "Tested from WinForms UI"
                };
                foreach(var path in _selectedImagePaths) {
                    req.Images.Add(ByteString.CopyFrom(File.ReadAllBytes(path)));
                }
                Log($"[UploadMistake] 发送上传请求，包含 {req.Images.Count} 张图片...");
                var res = await _client.UploadMistakeAsync(req);
                Log($"[UploadMistake] ✅ 成功! 生成的错题 ID: {res.Id}");
                
                // Switch to List tab automatically
                tabControl.SelectedTab = tabControl.TabPages[1];
            } catch(Exception ex) {
                Log($"[UploadMistake] ❌ 错误: {ex.Message}");
            } finally {
                btnUpload.Enabled = true;
            }
        };
        tab.Controls.Add(btnUpload);
    }

    private void SetupListTab(TabPage tab)
    {
        var pnlTop = new Panel { Dock = DockStyle.Top, Height = 60 };
        var btnGetList = new Button { Text = "🔄 查询我的错题本 (Refresh)", Location = new Point(20, 15), Width = 200, Height = 30, BackColor = Color.LightBlue };
        btnGetList.Click += async (s, e) => {
            try {
                btnGetList.Enabled = false;
                Log($"[GetMistakeList] 正在获取学生 {txtStudentId.Text} 的错题列表...");
                var req = new GetMistakeListRequest {
                    StudentId = txtStudentId.Text,
                    Page = 1,
                    Size = 50
                };
                var res = await _client.GetMistakeListAsync(req);
                dgvMistakes.DataSource = res.Items.Select(m => new {
                    m.Id, m.Subject, m.Grade, m.Content, Status = m.Status.ToString(), m.Date
                }).ToList();
                Log($"[GetMistakeList] ✅ 成功加载 {res.Items.Count} 条错题记录.");
            } catch(Exception ex) {
                Log($"[GetMistakeList] ❌ 错误: {ex.Message}");
            } finally {
                btnGetList.Enabled = true;
            }
        };
        pnlTop.Controls.Add(btnGetList);
        
        var lblTip = new Label { Text = "💡 提示：双击列表中的任意一行，可以将其 ID 自动填入【操作与分析】面板", Location = new Point(240, 22), AutoSize = true, ForeColor = Color.DimGray };
        pnlTop.Controls.Add(lblTip);

        tab.Controls.Add(pnlTop);

        dgvMistakes = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        dgvMistakes.CellDoubleClick += (s, e) => {
            if (e.RowIndex >= 0) {
                var id = dgvMistakes.Rows[e.RowIndex].Cells["Id"].Value?.ToString();
                if (!string.IsNullOrEmpty(id)) {
                    txtMistakeId.Text = id;
                    tabControl.SelectedTab = tabControl.TabPages[2]; // Switch to actions
                    Log($"[UI] 已将错题 ID [{id}] 复制到操作面板，即将切换...");
                }
            }
        };
        tab.Controls.Add(dgvMistakes);
    }

    private void SetupActionsTab(TabPage tab)
    {
        int y = 30;
        tab.Controls.Add(new Label { Text = "当前操作错题 ID:", Location = new Point(30, y), AutoSize = true, Font = new Font(this.Font, FontStyle.Bold) });
        txtMistakeId = new TextBox { Location = new Point(170, y), Width = 350 };
        tab.Controls.Add(txtMistakeId);

        y += 60;
        var grpInfo = new GroupBox { Text = "基本操作 (Basic Actions)", Location = new Point(20, y), Size = new Size(500, 100) };
        
        var btnGet = new Button { Text = "📖 获取详情 (Get)", Location = new Point(20, 30), Width = 140, Height = 40 };
        btnGet.Click += async (s, e) => {
            try {
                Log($"[GetMistake] 获取详情 ID: {txtMistakeId.Text} ...");
                var res = await _client.GetMistakeAsync(new GetMistakeRequest { MistakeId = txtMistakeId.Text, StudentId = txtStudentId.Text });
                Log($"[GetMistake] ✅ 详情: {res.Content} | 正确答案: {res.CorrectAnswer} | 状态: {res.Status}");
            } catch(Exception ex) { Log($"[GetMistake] ❌ 错误: {ex.Message}"); }
        };
        grpInfo.Controls.Add(btnGet);

        var btnMarkProcessed = new Button { Text = "✔️ 标记已处理", Location = new Point(170, 30), Width = 140, Height = 40 };
        btnMarkProcessed.Click += async (s, e) => {
            try {
                Log($"[MarkMistakeProcessed] 标记处理 ID: {txtMistakeId.Text} ...");
                var res = await _client.MarkMistakeProcessedAsync(new IdRequest { Id = txtMistakeId.Text });
                Log($"[MarkProcessed] ✅ 操作结果: {(res.Success ? "成功" : "失败 - " + res.ErrorMessage)}");
            } catch(Exception ex) { Log($"[MarkProcessed] ❌ 错误: {ex.Message}"); }
        };
        grpInfo.Controls.Add(btnMarkProcessed);

        var btnDelete = new Button { Text = "🗑️ 删除错题", Location = new Point(320, 30), Width = 140, Height = 40, ForeColor = Color.Red };
        btnDelete.Click += async (s, e) => {
            if (MessageBox.Show("确定要删除这条错题吗？（仅未处理状态可删除）", "警告", MessageBoxButtons.YesNo) == DialogResult.Yes) {
                try {
                    Log($"[DeleteMistake] 删除错题 ID: {txtMistakeId.Text} ...");
                    var res = await _client.DeleteMistakeAsync(new IdRequest { Id = txtMistakeId.Text });
                    Log($"[DeleteMistake] ✅ 操作结果: {(res.Success ? "成功" : "失败 - " + res.ErrorMessage)}");
                } catch(Exception ex) { Log($"[DeleteMistake] ❌ 错误: {ex.Message}"); }
            }
        };
        grpInfo.Controls.Add(btnDelete);
        tab.Controls.Add(grpInfo);
    }

    private void Log(string message)
    {
        if (InvokeRequired) {
            Invoke(new Action(() => Log(message)));
            return;
        }
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        txtLog.ScrollToCaret();
    }
}