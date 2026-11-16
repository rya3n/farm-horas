using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace FarmHoras
{
    internal sealed class MainWindow : Form
    {
        private const string FineAdjustmentAction = "Ajuste fino";
        private static readonly Color Background = Color.FromArgb(17, 23, 32);
        private static readonly Color PanelColor = Color.FromArgb(24, 33, 46);
        private static readonly Color RowAlternate = Color.FromArgb(27, 38, 52);
        private static readonly Color TextColor = Color.FromArgb(237, 244, 250);
        private static readonly Color Muted = Color.FromArgb(166, 186, 205);
        private static readonly Color Accent = Color.FromArgb(103, 225, 198);
        private static readonly Color Warning = Color.FromArgb(255, 190, 156);

        private readonly Dictionary<uint, DataGridViewRow> rows = new Dictionary<uint, DataGridViewRow>();
        private readonly Dictionary<uint, IdleSession> sessions = new Dictionary<uint, IdleSession>();
        private readonly HashSet<uint> pendingGoalRemovals = new HashSet<uint>();
        private readonly SteamPlaytime steamPlaytime = new SteamPlaytime();
        private readonly Timer refresh = new Timer { Interval = 250 };
        private readonly TextBox appIds = new TextBox();
        private readonly DataGridView grid = new DataGridView();
        private readonly Label steamStatus = new Label();
        private readonly Label feedback = new Label();
        private readonly Label sessionCount = new Label();
        private readonly Button addGames = new Button();
        private readonly Button startSelected = new Button();
        private readonly Button startAll = new Button();
        private readonly Button removeSelected = new Button();
        private readonly Button stopAll = new Button();
        private readonly string dataDirectory;
        private readonly string logPath;
        private readonly SavedGames savedGames;
        private readonly PlaytimeGuard playtimeGuard;
        private bool gamesLoaded;
        private bool steamRunning;
        private DateTime nextSteamCheck;
        private DateTime nextTotalCheck;
        private DateTime nextGoalSave;
        private Dictionary<uint, int> steamTotals;
        private uint steamAccount;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        internal MainWindow()
        {
            var dataOverride = Environment.GetEnvironmentVariable("FARM_HORAS_DATA_DIR");
            dataDirectory = string.IsNullOrWhiteSpace(dataOverride)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FarmHoras")
                : Path.GetFullPath(dataOverride);
            logPath = Path.Combine(dataDirectory, "farm-horas.log");
            savedGames = new SavedGames(dataDirectory, logPath);
            playtimeGuard = new PlaytimeGuard(dataDirectory);

            BuildLayout();
            LoadGames();
            refresh.Tick += delegate { RefreshSessions(); };
            RefreshSessions();
            refresh.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int enabled = 1;
            DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
        }

        private void BuildLayout()
        {
            SuspendLayout();
            Text = "Farm Horas";
            AccessibleName = Text;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = new Size(1026, 490);
            ClientSize = new Size(1094, 570);
            Font = new Font("Segoe UI", 10);
            BackColor = Background;
            ForeColor = TextColor;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(32, 23, 32, 22), ColumnCount = 1, RowCount = 8 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 52, 30, 31, 56, 31, 40 })
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            }

            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));

            var header = ActionColumns(160);
            var title = MakeLabel("Farm Horas", 29, FontStyle.Bold, TextColor);
            header.Controls.Add(title, 0, 0);
            steamStatus.Dock = DockStyle.Fill;
            steamStatus.Font = new Font(Font, FontStyle.Bold);
            steamStatus.TextAlign = ContentAlignment.MiddleRight;
            steamStatus.Margin = Padding.Empty;
            header.Controls.Add(steamStatus, 1, 0);
            layout.Controls.Add(header, 0, 0);
            layout.Controls.Add(MakeLabel("Meta de 3.333 h com parada inicial de segurança e ajuste fino.", 10, FontStyle.Regular, Muted), 0, 1);

            layout.Controls.Add(MakeLabel("&AppIDs para adicionar", 10, FontStyle.Bold, TextColor), 0, 2);
            var entry = ActionColumns(154, 162);
            var field = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0), Padding = new Padding(13, 7, 13, 6), BackColor = PanelColor };
            appIds.Dock = DockStyle.Fill;
            appIds.Multiline = true;
            appIds.Font = new Font("Segoe UI", 11);
            appIds.BorderStyle = BorderStyle.None;
            appIds.BackColor = PanelColor;
            appIds.ForeColor = TextColor;
            appIds.AccessibleName = "AppIDs dos jogos";
            field.Controls.Add(appIds);
            entry.Controls.Add(field, 0, 0);
            StyleButton(addGames, "&Adicionar à lista", true);
            addGames.Margin = new Padding(0, 0, 10, 0);
            addGames.Click += delegate { SaveInput(false); };
            entry.Controls.Add(addGames, 1, 0);
            var addAndStart = new Button();
            StyleButton(addAndStart, "Adicionar e &iniciar", false);
            addAndStart.Click += delegate { SaveInput(true); };
            entry.Controls.Add(addAndStart, 2, 0);
            layout.Controls.Add(entry, 0, 3);
            AcceptButton = addGames;

            feedback.Dock = DockStyle.Fill;
            feedback.Margin = Padding.Empty;
            feedback.TextAlign = ContentAlignment.MiddleLeft;
            feedback.AutoEllipsis = true;
            ShowFeedback("Cole vários AppIDs, separados por vírgulas, espaços ou linhas.", false);
            layout.Controls.Add(feedback, 0, 4);

            var sessionsHeader = ActionColumns(300);
            sessionsHeader.Controls.Add(MakeLabel("Jogos salvos", 15, FontStyle.Bold, TextColor), 0, 0);
            sessionCount.Dock = DockStyle.Fill;
            sessionCount.ForeColor = Muted;
            sessionCount.TextAlign = ContentAlignment.MiddleRight;
            sessionCount.Margin = Padding.Empty;
            sessionsHeader.Controls.Add(sessionCount, 1, 0);
            layout.Controls.Add(sessionsHeader, 0, 5);

            ConfigureGrid();
            layout.Controls.Add(grid, 0, 6);
            var actions = ActionColumns(142, 166, 145, 120);
            actions.Margin = new Padding(0, 9, 0, 0);
            var openLog = new LinkLabel
            {
                Text = "Abrir log",
                Dock = DockStyle.Left,
                Width = 90,
                TextAlign = ContentAlignment.MiddleLeft,
                LinkColor = Muted,
                ActiveLinkColor = Accent,
                VisitedLinkColor = Muted,
                Margin = Padding.Empty
            };
            openLog.LinkClicked += delegate { OpenLog(); };
            actions.Controls.Add(openLog, 0, 0);
            StyleButton(removeSelected, "&Remover da lista", false);
            removeSelected.Margin = new Padding(0, 0, 10, 0);
            removeSelected.Click += delegate { RemoveGames(); };
            actions.Controls.Add(removeSelected, 1, 0);
            StyleButton(startSelected, "Iniciar &selecionados", false);
            startSelected.Margin = new Padding(0, 0, 10, 0);
            startSelected.Click += delegate { StartGames(SelectedIds()); };
            actions.Controls.Add(startSelected, 2, 0);
            StyleButton(startAll, "Iniciar t&odos", true);
            startAll.Margin = new Padding(0, 0, 10, 0);
            startAll.Click += delegate { StartGames(ListedIds()); };
            actions.Controls.Add(startAll, 3, 0);
            StyleButton(stopAll, "Parar &todas", false);
            stopAll.Click += delegate { RequestStopAll(false); };
            actions.Controls.Add(stopAll, 4, 0);
            layout.Controls.Add(actions, 0, 7);

            Controls.Add(layout);
            ActiveControl = appIds;
            ResumeLayout();
        }

        private static TableLayoutPanel ActionColumns(params int[] rightWidths)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = rightWidths.Length + 1, RowCount = 1 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int width in rightWidths)
            {
                panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
            }
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            return panel;
        }

        private static Label MakeLabel(string text, float size, FontStyle style, Color color)
        {
            return new Label
            {
                Text = text,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Font = new Font("Segoe UI", size, style),
                ForeColor = color
            };
        }

        private static void StyleButton(Button button, string label, bool primary)
        {
            button.Dock = DockStyle.Fill;
            button.Text = label;
            button.Cursor = Cursors.Hand;
            button.Margin = Padding.Empty;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(65, 86, 108);
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(138, 238, 217) : RowAlternate;
            button.BackColor = primary ? Accent : PanelColor;
            button.ForeColor = primary ? Background : TextColor;
            button.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        }

        private void ConfigureGrid()
        {
            grid.Dock = DockStyle.Fill;
            grid.Margin = Padding.Empty;
            grid.BackgroundColor = PanelColor;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.GridColor = Background;
            grid.EnableHeadersVisualStyles = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeColumns = false;
            grid.AllowUserToResizeRows = false;
            grid.MultiSelect = true;
            grid.ReadOnly = true;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 35;
            grid.RowTemplate.Height = 40;
            grid.AccessibleName = "Jogos salvos por AppID";
            grid.DefaultCellStyle.BackColor = PanelColor;
            grid.DefaultCellStyle.ForeColor = TextColor;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(43, 65, 85);
            grid.DefaultCellStyle.SelectionForeColor = TextColor;
            grid.DefaultCellStyle.Padding = new Padding(6, 0, 4, 0);
            grid.AlternatingRowsDefaultCellStyle.BackColor = RowAlternate;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(32, 47, 63);
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 4, 0);
            grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;

            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "appId", HeaderText = "AppID", FillWeight = 99, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "state", HeaderText = "Status", FillWeight = 112, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "elapsed", HeaderText = "Sessão", FillWeight = 97, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "steamTotal", HeaderText = "Total na Steam", FillWeight = 117, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "localEstimate",
                HeaderText = "Estimativa",
                ToolTipText = "Total estimado localmente pelo Farm Horas.",
                FillWeight = 115,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "untilGoal",
                HeaderText = "Até 3.333 h",
                ToolTipText = "Contagem até a meta pelo relógio local, em horas, minutos e segundos.",
                FillWeight = 109,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "untilStop", HeaderText = "Até a parada", FillWeight = 112, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewButtonColumn
            {
                Name = "action",
                HeaderText = "Ação",
                FlatStyle = FlatStyle.Flat,
                FillWeight = 106,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            grid.CellContentClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == grid.Columns["action"].Index)
                {
                    var id = (uint)grid.Rows[e.RowIndex].Tag;
                    IdleSession session;
                    if (sessions.TryGetValue(id, out session))
                    {
                        StopSession(id, session);
                    }
                    else
                    {
                        bool fineAdjustment = Equals(grid.Rows[e.RowIndex].Cells["action"].Value, FineAdjustmentAction);
                        StartSession(id, fineAdjustment);
                    }
                }
            };
            grid.SelectionChanged += delegate { RefreshSelectionActions(); };
        }

        private void SaveInput(bool start)
        {
            string invalid;
            var ids = SavedGames.ParseIds(appIds.Text, out invalid);
            if (invalid != null || ids.Count == 0)
            {
                ShowFeedback(invalid == null ? "Digite ao menos um AppID." : "AppID inválido: " + invalid, true);
                appIds.Focus();
                return;
            }

            if (!gamesLoaded && !LoadGames())
            {
                return;
            }

            var newIds = ids.FindAll(id => !rows.ContainsKey(id));
            if (newIds.Count > 0)
            {
                var updated = ListedIds();
                updated.AddRange(newIds);
                if (!TrySaveGames(updated))
                {
                    return;
                }

                foreach (uint id in newIds)
                {
                    EnsureRow(id);
                }

                grid.ClearSelection();
                foreach (uint id in newIds)
                {
                    rows[id].Selected = true;
                }

                grid.FirstDisplayedScrollingRowIndex = rows[newIds[0]].Index;
            }

            appIds.Clear();
            RefreshCount();
            if (start)
            {
                StartGames(ids);
            }
            else if (newIds.Count == 0)
            {
                ShowFeedback("Os AppIDs informados já estão na lista.", false);
            }
            else
            {
                ShowFeedback(newIds.Count == 1 ? "1 jogo adicionado à lista." : newIds.Count + " jogos adicionados à lista.", false);
            }
        }

        private void StartGames(List<uint> ids)
        {
            var pending = ids.FindAll(id => !sessions.ContainsKey(id));
            if (pending.Count == 0)
            {
                ShowFeedback("Os jogos escolhidos já têm sessões abertas.", false);
                return;
            }

            if (!ReadyToStart())
            {
                return;
            }

            var launching = 0;
            foreach (uint id in pending)
            {
                if (LaunchSession(id, false))
                {
                    launching++;
                }
            }

            if (launching == pending.Count)
            {
                ShowFeedback("Aguardando confirmação da Steam.", false);
            }
            else if (launching > 0)
            {
                int parked = pending.Count - launching;
                string parkedCount = parked == 1 ? "1 jogo ficou parado" : parked + " jogos ficaram parados";
                ShowFeedback(launching + " iniciando · " + parkedCount + ". Veja o status de cada jogo.", false);
            }
        }

        private List<uint> ListedIds()
        {
            var ids = new List<uint>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                ids.Add((uint)row.Tag);
            }

            return ids;
        }

        private List<uint> SelectedIds()
        {
            var ids = new List<uint>();
            foreach (DataGridViewRow row in grid.SelectedRows)
            {
                if (row.Tag is uint)
                {
                    ids.Add((uint)row.Tag);
                }
            }

            return ids;
        }

        private void RemoveGames()
        {
            var selected = SelectedIds();
            if (selected.Count == 0 || selected.Exists(sessions.ContainsKey) || !TryRemoveSavedGames(selected))
            {
                return;
            }

            RefreshCount();
            ShowFeedback(selected.Count == 1 ? "1 jogo removido da lista." : selected.Count + " jogos removidos da lista.", false);
        }

        private bool TryRemoveSavedGames(List<uint> ids)
        {
            var removal = new HashSet<uint>(ids);
            var updated = ListedIds().FindAll(id => !removal.Contains(id));
            if (!TrySaveGames(updated))
            {
                return false;
            }

            foreach (uint id in ids)
            {
                grid.Rows.Remove(rows[id]);
                rows.Remove(id);
                pendingGoalRemovals.Remove(id);
            }

            return true;
        }

        private void StartSession(uint id, bool fineAdjustment)
        {
            if (ReadyToStart() && LaunchSession(id, fineAdjustment))
            {
                ShowFeedback("Aguardando confirmação da Steam para " + id + ".", false);
            }
        }

        private bool ReadyToStart()
        {
            UpdateSteamStatus();
            if (!steamRunning)
            {
                ShowFeedback("Abra a Steam, entre na sua conta e tente novamente.", true);
                return false;
            }

            if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, IdleWorker.Library)))
            {
                ShowFeedback("A biblioteca steam_api64.dll deve ficar ao lado do executável.", true);
                return false;
            }

            RefreshSteamTotals();
            return true;
        }

        private bool LaunchSession(uint id, bool fineAdjustment)
        {
            if (QueueGoalRemovalIfReached(id))
            {
                var message = "O AppID " + id + " já atingiu 3.333 horas na Steam.";
                UpdateRow(id, "Meta atingida", TimeSpan.Zero, "Meta", Accent);
                rows[id].Cells["state"].ToolTipText = message;
                ShowFeedback(message, false);
                return false;
            }

            int minutes;
            if (steamAccount == 0 || !TryGetSteamMinutes(id, out minutes))
            {
                ShowGuardBlock(id, "Confira o total na Steam. O programa ainda não conseguiu lê-lo para " + id + ".");
                return false;
            }

            PlaytimeBudget budget;
            PlaytimeStage stage;
            try
            {
                budget = playtimeGuard.Reserve(steamAccount, id, minutes, fineAdjustment, out stage);
            }
            catch (IOException exception)
            {
                ReportGuardFailure(id, exception);
                return false;
            }
            catch (InvalidDataException exception)
            {
                ReportGuardFailure(id, exception);
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                ReportGuardFailure(id, exception);
                return false;
            }

            if (budget == null)
            {
                ShowGuardBlock(id, GuardMessage(stage));
                if (stage == PlaytimeStage.WaitingForSteam)
                {
                    feedback.Tag = id;
                }
                return false;
            }

            try
            {
                sessions.Add(id, new IdleSession(id, budget));
                rows[id].Cells["state"].ToolTipText = string.Empty;
                UpdateRow(id, "Iniciando", TimeSpan.Zero, "Parar", Muted);
                WriteLog(id + (fineAdjustment ? " | Iniciando rodada de ajuste fino." : " | Inicializando a Steamworks."));
                RefreshCount();
                return true;
            }
            catch (Win32Exception exception)
            {
                ReportLaunchFailure(id, exception);
            }
            catch (IOException exception)
            {
                ReportLaunchFailure(id, exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                ReportLaunchFailure(id, exception);
            }
            catch (InvalidOperationException exception)
            {
                ReportLaunchFailure(id, exception);
            }

            if (!sessions.ContainsKey(id))
            {
                RecordGuardUsage(id, budget, false);
            }
            return false;
        }

        private void ShowGuardBlock(uint id, string message)
        {
            UpdateRow(id, "Protegido", TimeSpan.Zero, "Iniciar", Warning);
            rows[id].Cells["state"].ToolTipText = message;
            UpdateGuardCells(id, rows[id], null);
            ShowFeedback(message, true);
        }

        private static string GuardMessage(PlaytimeStage stage)
        {
            switch (stage)
            {
                case PlaytimeStage.FineAdjustment:
                    return "Use Ajuste fino na linha do jogo para iniciar uma rodada curta.";
                case PlaytimeStage.WaitingForSteam:
                    return "Aguarde a Steam registrar as horas da sessão anterior.";
                case PlaytimeStage.InsufficientTime:
                    return "A folga estimada é insuficiente para outra rodada de ajuste fino.";
                case PlaytimeStage.Interrupted:
                    return "O encerramento da sessão anterior ficou sem confirmação no registro local.";
                default:
                    return "Use Iniciar para rodar até a parada inicial de segurança.";
            }
        }

        private void ReportGuardFailure(uint id, Exception exception)
        {
            ShowGuardBlock(id, "Não foi possível salvar ou ler a proteção de horas para " + id + ".");
            WriteLog(id + " | " + exception.Message);
        }

        private void RecordGuardUsage(uint id, PlaytimeBudget budget, bool workerMayHavePlayed)
        {
            try
            {
                playtimeGuard.Settle(budget, workerMayHavePlayed);
            }
            catch (IOException exception)
            {
                ReportGuardFailure(id, exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                ReportGuardFailure(id, exception);
            }
        }

        private void ReportLaunchFailure(uint id, Exception exception)
        {
            UpdateRow(id, "Falhou", TimeSpan.Zero, "Iniciar", Warning);
            ShowFeedback("Falha ao iniciar o AppID " + id + ". Consulte o log.", true);
            WriteLog(id + " | " + exception.Message);
        }

        private void EnsureRow(uint id)
        {
            if (!rows.ContainsKey(id))
            {
                int index = grid.Rows.Add(id.ToString(CultureInfo.InvariantCulture), "Pronto", "00:00:00", "—", "—", "—", "—", "Iniciar");
                var row = grid.Rows[index];
                row.Tag = id;
                rows.Add(id, row);
                UpdateTotalCell(id, row);
                UpdateGuardCells(id, row, null);
                QueueGoalRemovalIfReached(id);
            }
        }

        private void UpdateRow(uint id, string state, TimeSpan elapsed, string action, Color color)
        {
            var row = rows[id];
            row.Cells["state"].Style.ForeColor = color;
            row.Cells["state"].Style.SelectionForeColor = color;
            row.Cells["state"].Value = state;
            row.Cells["elapsed"].Value = FormatDuration((long)elapsed.TotalSeconds);
            row.Cells["action"].Value = action;
        }

        private void UpdateSteamStatus()
        {
            nextSteamCheck = DateTime.UtcNow.AddSeconds(1);
            var running = SteamClient.IsRunning();
            if (running != steamRunning)
            {
                nextTotalCheck = DateTime.MinValue;
            }
            steamRunning = running;
            steamStatus.Text = steamRunning ? "●  Steam aberta" : "○  Steam fechada";
            steamStatus.ForeColor = steamRunning ? Accent : Muted;
        }

        private void RefreshSteamTotals()
        {
            nextTotalCheck = DateTime.UtcNow.AddSeconds(15);
            steamTotals = steamRunning ? steamPlaytime.Read() : null;
            steamAccount = steamTotals == null ? 0 : steamPlaytime.AccountId;
            foreach (var pair in rows)
            {
                UpdateTotalCell(pair.Key, pair.Value);
                IdleSession session;
                sessions.TryGetValue(pair.Key, out session);
                UpdateGuardCells(pair.Key, pair.Value, session);
                QueueGoalRemovalIfReached(pair.Key);
            }
        }

        private bool QueueGoalRemovalIfReached(uint id)
        {
            int minutes;
            if (TryGetSteamMinutes(id, out minutes) && minutes >= SteamPlaytime.TargetMinutes)
            {
                pendingGoalRemovals.Add(id);
                return true;
            }

            return pendingGoalRemovals.Contains(id);
        }

        private bool TryGetSteamMinutes(uint id, out int minutes)
        {
            minutes = 0;
            return steamTotals != null && steamTotals.TryGetValue(id, out minutes);
        }

        private void UpdateTotalCell(uint id, DataGridViewRow row)
        {
            int minutes;
            var cell = row.Cells["steamTotal"];
            bool hasTotal = TryGetSteamMinutes(id, out minutes);
            cell.Value = hasTotal ? SteamPlaytime.FormatMinutes(minutes) : "—";
            cell.ToolTipText = hasTotal
                ? "Total registrado no cliente Steam. Meta de 3.333 horas."
                : "O cliente Steam ainda não forneceu um total para este AppID.";
        }

        private void UpdateGuardCells(uint id, DataGridViewRow row, IdleSession session)
        {
            var estimateCell = row.Cells["localEstimate"];
            var goalCell = row.Cells["untilGoal"];
            var remainingCell = row.Cells["untilStop"];
            int minutes;
            long estimate = 0;
            long remaining = 0;
            bool available = TryGetSteamMinutes(id, out minutes) && steamAccount != 0;
            if (session != null)
            {
                available = available && steamAccount == session.Budget.AccountId;
                estimate = session.Budget.EstimatedTotalSeconds;
                if (!session.Stopping)
                {
                    remaining = Math.Min(session.Budget.SecondsUntilDeadline,
                        PlaytimeGuard.RemainingSeconds(session.Budget.LimitSeconds, estimate));
                }
            }
            else
            {
                PlaytimePlan plan = default(PlaytimePlan);
                available = available && playtimeGuard.TryGetPlan(steamAccount, id, minutes, out plan);
                estimate = plan.EstimateSeconds;
                remaining = plan.SessionSeconds;
                UpdateStartAction(id, row, available, plan.Stage);
            }

            estimateCell.Value = available ? SteamPlaytime.FormatMinutes(estimate / 60) : "—";
            estimateCell.ToolTipText = available
                ? "Estimativa conservadora do Farm Horas: " + FormatDuration(estimate) + "."
                : "Estimativa indisponível enquanto o total ou o registro local estiver indisponível.";
            goalCell.Value = available ? FormatDuration(Math.Max(0, SteamPlaytime.TargetSeconds - estimate)) : "—";
            goalCell.ToolTipText = "Baseado na estimativa local. A sessão segue o limite indicado em Até a parada.";
            remainingCell.Value = available ? FormatDuration(remaining) : "—";
            remainingCell.ToolTipText = "Tempo disponível até a parada de segurança, em horas, minutos e segundos.";
            Color color = available && remaining <= PlaytimeGuard.MarginMinutes * 60 ? Warning : TextColor;
            remainingCell.Style.ForeColor = color;
            remainingCell.Style.SelectionForeColor = color;
        }

        private void UpdateStartAction(uint id, DataGridViewRow row, bool available, PlaytimeStage stage)
        {
            var cell = row.Cells["action"];
            if (Equals(cell.Value, "Meta"))
            {
                return;
            }

            cell.ToolTipText = available && stage != PlaytimeStage.Normal ? GuardMessage(stage) : string.Empty;
            if (!available || stage == PlaytimeStage.Normal)
            {
                cell.Value = "Iniciar";
            }
            else if (stage == PlaytimeStage.FineAdjustment)
            {
                cell.Value = FineAdjustmentAction;
                if (Equals(feedback.Tag, id))
                {
                    ShowFeedback("A Steam atualizou o AppID " + id + ". O ajuste fino já está disponível.", false);
                }
                if (Equals(row.Cells["state"].Value, "Protegido"))
                {
                    var stateCell = row.Cells["state"];
                    stateCell.Value = "Pronto";
                    stateCell.Style.ForeColor = TextColor;
                    stateCell.Style.SelectionForeColor = TextColor;
                    stateCell.ToolTipText = GuardMessage(stage);
                }
            }
            else if (stage == PlaytimeStage.WaitingForSteam)
            {
                cell.Value = "Aguarde";
            }
            else
            {
                cell.Value = stage == PlaytimeStage.Interrupted ? "Bloqueado" : "Sem folga";
            }
        }

        private static string FormatDuration(long seconds)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}",
                seconds / 3600, seconds / 60 % 60, seconds % 60);
        }

        private void RefreshSessions()
        {
            if (DateTime.UtcNow >= nextSteamCheck)
            {
                UpdateSteamStatus();
                if (!steamRunning && sessions.Count > 0)
                {
                    RequestStopAll(true);
                }
            }

            if (rows.Count > 0 && DateTime.UtcNow >= nextTotalCheck)
            {
                RefreshSteamTotals();
            }

            var finished = new List<uint>();
            foreach (var pair in sessions)
            {
                uint id = pair.Key;
                var session = pair.Value;
                if (session.IsExited)
                {
                    FinishSession(id, session);
                    finished.Add(id);
                    continue;
                }

                if (!session.Stopping && pendingGoalRemovals.Contains(id))
                {
                    int reported;
                    if (steamAccount == session.Budget.AccountId && TryGetSteamMinutes(id, out reported))
                    {
                        session.Budget.Observe(reported);
                    }
                    session.GoalReached = true;
                    StopSession(id, session);
                }
                else if (!session.Stopping && CheckSessionSafety(id, session))
                {
                    StopSession(id, session);
                }
                else if (session.CheckReady())
                {
                    UpdateRow(id, session.Budget.IsFineAdjustment ? FineAdjustmentAction : "Em execução", session.Elapsed, "Parar", Accent);
                    WriteLog(id + " | Sessão iniciada.");
                    ShowFeedback("Sessão iniciada para " + id + ".", false);
                }
                else if (!session.Started && !session.Stopping && session.StartExpired)
                {
                    session.TimedOut = true;
                    StopSession(id, session);
                }

                if (session.Stopping && session.StopExpired)
                {
                    ForceStop(id, session);
                }

                if (session.Started && !session.Stopping)
                {
                    UpdateRow(id, session.Budget.IsFineAdjustment ? FineAdjustmentAction : "Em execução", session.Elapsed, "Parar", Accent);
                }

                UpdateGuardCells(id, rows[id], session);
            }

            foreach (uint id in finished)
            {
                sessions.Remove(id);
            }
            if (finished.Count > 0)
            {
                // Decide whether another round is available using a read after the workers exited.
                RefreshSteamTotals();
            }

            RemoveGamesAtGoal();
            RefreshCount();
        }

        private bool CheckSessionSafety(uint id, IdleSession session)
        {
            int minutes;
            if (session.Budget.Expired)
            {
                session.SafetyReason = CutoffMessage(id, session.Budget);
            }
            else if (steamAccount != session.Budget.AccountId || !TryGetSteamMinutes(id, out minutes))
            {
                session.SafetyReason = "A sessão de " + id + " foi encerrada porque o total da Steam ficou indisponível.";
            }
            else
            {
                bool jumped = session.Budget.Observe(minutes);
                if (session.Budget.EstimatedTotalSeconds >= session.Budget.LimitSeconds)
                {
                    session.SafetyReason = CutoffMessage(id, session.Budget);
                }
                else if (jumped)
                {
                    session.SafetyReason = "A Steam informou um total acima da estimativa para " + id + ". Confira as horas antes de retomar.";
                }
            }

            return session.SafetyReason != null;
        }

        private static string CutoffMessage(uint id, PlaytimeBudget budget)
        {
            return budget.IsFineAdjustment
                ? "Rodada concluída para " + id + ". Aguarde o total atualizar na Steam."
                : "Limite de segurança inicial atingido para " + id + ".";
        }

        private void RemoveGamesAtGoal()
        {
            if (pendingGoalRemovals.Count == 0 || DateTime.UtcNow < nextGoalSave)
            {
                return;
            }

            var completed = new List<uint>();
            foreach (uint id in pendingGoalRemovals)
            {
                if (!sessions.ContainsKey(id))
                {
                    completed.Add(id);
                }
            }

            if (completed.Count == 0)
            {
                return;
            }

            if (!TryRemoveSavedGames(completed))
            {
                nextGoalSave = DateTime.UtcNow.AddSeconds(15);
                return;
            }

            nextGoalSave = DateTime.MinValue;
            foreach (uint id in completed)
            {
                WriteLog(id + " | Removido da lista ao atingir 3.333 horas na Steam.");
            }

            ShowFeedback(completed.Count == 1
                ? "AppID " + completed[0] + " removido da lista ao atingir 3.333 horas."
                : completed.Count + " jogos removidos da lista ao atingir 3.333 horas.", false);
        }

        private void FinishSession(uint id, IdleSession session)
        {
            var elapsed = session.Elapsed;
            int exitCode = session.ExitCode;
            string message = null;
            string state = "Parada";
            if (session.GoalReached)
            {
                state = "Meta atingida";
                message = "Sessão de " + id + " encerrada ao atingir 3.333 horas na Steam.";
            }
            else if (session.SafetyReason != null || exitCode == IdleWorker.SafetyStopped)
            {
                state = session.Budget.IsFineAdjustment ? "Rodada encerrada" : "Protegido";
                message = session.SafetyReason ?? CutoffMessage(id, session.Budget);
            }
            else if (session.TimedOut)
            {
                state = "Tempo esgotado";
                message = "A Steam não confirmou a sessão de " + id + " a tempo.";
            }
            else if (session.SteamClosed)
            {
                state = "Steam fechada";
                message = "A Steam foi fechada. As sessões foram encerradas.";
            }
            else if (!session.Stopping)
            {
                state = "Falhou";
                switch (exitCode)
                {
                    case IdleWorker.SteamRejected:
                        message = "A Steam recusou " + id + ". Confira o AppID e se o jogo está na sua biblioteca.";
                        break;
                    case IdleWorker.LibraryUnavailable:
                        message = "A biblioteca Steamworks está ausente ou é incompatível.";
                        break;
                    case IdleWorker.AccountChanged:
                        message = "A conta Steam mudou ou ficou indisponível antes de iniciar " + id + ".";
                        break;
                    default:
                        message = "A sessão de " + id + " foi encerrada. Consulte o log.";
                        break;
                }
            }

            Color stateColor = session.GoalReached ? Accent : message == null ? Muted : Warning;
            UpdateRow(id, state, elapsed, session.GoalReached ? "Meta" : "Iniciar", stateColor);
            rows[id].Cells["state"].ToolTipText = message ?? "Sessão encerrada.";
            WriteLog(id + " | " + state + " (código " + exitCode + "). " + message);
            if (message != null)
            {
                ShowFeedback(message, !session.GoalReached);
                if (session.Budget.IsFineAdjustment && state == "Rodada encerrada")
                {
                    feedback.Tag = id;
                }
            }
            else if (feedback.ForeColor != Warning)
            {
                ShowFeedback("Sessão encerrada para " + id + ".", false);
            }

            RecordGuardUsage(id, session.Budget, WorkerMayHavePlayed(session, exitCode));
            session.Dispose();
        }

        private static bool WorkerMayHavePlayed(IdleSession session, int exitCode)
        {
            return session.Started || (exitCode != IdleWorker.SteamRejected && exitCode != IdleWorker.LibraryUnavailable &&
                exitCode != IdleWorker.AccountChanged && exitCode != IdleWorker.CancelledBeforeStart);
        }

        private void StopSession(uint id, IdleSession session)
        {
            session.RequestStop();
            UpdateRow(id, "Encerrando", session.Elapsed, "Aguarde", Muted);
        }

        private void RequestStopAll(bool steamClosed)
        {
            foreach (var pair in sessions)
            {
                if (!pair.Value.Stopping)
                {
                    pair.Value.SteamClosed = steamClosed;
                    StopSession(pair.Key, pair.Value);
                }
            }

            if (!steamClosed)
            {
                ShowFeedback("Encerrando as sessões abertas.", false);
            }
        }

        private void ForceStop(uint id, IdleSession session)
        {
            try
            {
                session.ForceStop();
            }
            catch (Win32Exception exception)
            {
                WriteLog(id + " | Falha ao encerrar processo: " + exception.Message);
            }
        }

        private void RefreshCount()
        {
            int active = 0;
            bool canStop = false;
            foreach (var session in sessions.Values)
            {
                if (!session.Stopping)
                {
                    canStop = true;
                    if (session.Started)
                    {
                        active++;
                    }
                }
            }

            string savedCount = rows.Count == 1 ? "1 jogo adicionado" : rows.Count + " jogos adicionados";
            string runningCount = active == 1 ? "1 ativa" : active + " ativas";
            sessionCount.Text = savedCount + "  ·  " + runningCount;
            startAll.Enabled = rows.Count > sessions.Count;
            stopAll.Enabled = canStop;
            RefreshSelectionActions();
        }

        private void RefreshSelectionActions()
        {
            var selection = SelectedIds();
            var hasIdle = selection.Exists(id => !sessions.ContainsKey(id));
            startSelected.Enabled = hasIdle;
            removeSelected.Enabled = hasIdle && !selection.Exists(sessions.ContainsKey);
        }

        private void ShowFeedback(string message, bool warning)
        {
            feedback.Tag = null;
            feedback.Text = message;
            feedback.ForeColor = warning ? Warning : Muted;
        }

        private bool LoadGames()
        {
            try
            {
                foreach (uint id in savedGames.Load())
                {
                    EnsureRow(id);
                }

                gamesLoaded = true;
                grid.ClearSelection();
                return true;
            }
            catch (IOException exception)
            {
                ReportFileFailure("Falha ao carregar a lista de jogos salvos.", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                ReportFileFailure("Falha ao carregar a lista de jogos salvos.", exception);
            }

            return false;
        }

        private bool TrySaveGames(List<uint> ids)
        {
            try
            {
                savedGames.Save(ids);
                return true;
            }
            catch (IOException exception)
            {
                ReportFileFailure("Falha ao atualizar a lista de jogos salvos.", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                ReportFileFailure("Falha ao atualizar a lista de jogos salvos.", exception);
            }

            return false;
        }

        private void WriteLog(string message)
        {
            try
            {
                Directory.CreateDirectory(dataDirectory);
                File.AppendAllText(logPath, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
                    + " | " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException exception)
            {
                Debug.WriteLine(exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                Debug.WriteLine(exception.Message);
            }
        }

        private void ReportFileFailure(string message, Exception exception)
        {
            ShowFeedback(message, true);
            WriteLog(message + " " + exception.Message);
        }

        private void OpenLog()
        {
            try
            {
                if (!File.Exists(logPath))
                {
                    ShowFeedback("O log será criado quando houver atividade.", false);
                    return;
                }

                Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                ShowFeedback("Não foi possível abrir o log em " + logPath, true);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel)
            {
                return;
            }

            refresh.Stop();
            foreach (var session in sessions.Values)
            {
                session.RequestStop();
            }

            const int graceMilliseconds = 1200;
            const int confirmationMilliseconds = 400;
            var closing = Stopwatch.StartNew();
            foreach (var pair in sessions)
            {
                if (!pair.Value.WaitForExit(Math.Max(0, graceMilliseconds - (int)closing.ElapsedMilliseconds)))
                {
                    ForceStop(pair.Key, pair.Value);
                }
            }

            var confirming = Stopwatch.StartNew();
            foreach (var pair in sessions)
            {
                // Process.Kill requests termination. Wait for the process handle before settling its reservation.
                if (pair.Value.WaitForExit(Math.Max(0, confirmationMilliseconds - (int)confirming.ElapsedMilliseconds)))
                {
                    RecordGuardUsage(pair.Key, pair.Value.Budget, WorkerMayHavePlayed(pair.Value, pair.Value.ExitCode));
                    WriteLog(pair.Key + " | Encerrada ao fechar o Farm Horas.");
                }
                else
                {
                    WriteLog(pair.Key + " | Encerramento sem confirmação. Reserva de tempo preservada.");
                }

                pair.Value.Dispose();
            }

            sessions.Clear();
            nextGoalSave = DateTime.MinValue;
            RemoveGamesAtGoal();
            refresh.Dispose();
        }
    }
}
