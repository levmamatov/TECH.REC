using System.Data;
using System.Data.SqlClient;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using static Technic_accounting.NotificationsControl;
namespace Technic_accounting
{
    public partial class MainWindow : Window
    {
        private readonly string connString;
        private DispatcherTimer _refreshTimer;
        private static HttpClient _client;
        private bool _isGroupMode = false;

        public MainWindow()
        {
            InitializeComponent();

            var config = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

            connString = config.GetConnectionString("DefaultConnection")
                ?? @"Server=26.197.58.130,1433; Database=CourseworkPractice; User ID=sa; Password=12345; TrustServerCertificate=True;";

            string apiAddress = config["ApiSettings:BaseAddress"] ?? "http://192.168.0.170:5000/";

            _client = new HttpClient
            {
                BaseAddress = new Uri(apiAddress)
            };
            LoadFilters();
            LoadData();
            RefreshTables();

            InitRefreshTimer();
            RefreshAllDashboardData();
            
        }

        private void InitRefreshTimer()
        {
            _refreshTimer = new DispatcherTimer();
            _refreshTimer.Interval = TimeSpan.FromSeconds(12);
            _refreshTimer.Tick += RefreshTimer_Tick;
            _refreshTimer.Start();
        }
        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            RefreshAllDashboardData();
        }

        private void ResetCountersToDefault()
        {
            if (TxtMainTotalLaptops != null) TxtMainTotalLaptops.Text = "0";
            if (TxtMainActiveStudents != null) TxtMainActiveStudents.Text = "0";
            if (TxtMainOpenRequests != null) TxtMainOpenRequests.Text = "0";
            if (TxtMainAuthErrors != null) TxtMainAuthErrors.Text = "0";

            if (TxtStatInSession != null) TxtStatInSession.Text = "0";
            if (TxtStatIssue != null) TxtStatIssue.Text = "0";
            if (TxtStatAvailable != null) TxtStatAvailable.Text = "0";
        }

        /// <summary>
        /// Главный метод сбора актуальной статистики (Ультра-безопасная версия)
        /// </summary>
        private void RefreshAllDashboardData()
        {
            // Шаг 1: Сразу ставим нули везде по дефолту, чтобы поля не были пустыми
            ResetCountersToDefault();

            try
            {
                using (SqlConnection conn = new SqlConnection(connString))
                {
                    conn.Open();

                    // Шаг 2: Вытягиваем сырые данные из SQL Server
                    string totalLaptops = ExecuteScalarQuery(conn, "SELECT COUNT(*) FROM Equipment");
                    string activeStudents = ExecuteScalarQuery(conn, "SELECT COUNT(DISTINCT user_id) FROM UsageLog WHERE end_date IS NULL");
                    string openRequests = ExecuteScalarQuery(conn, "SELECT COUNT(*) FROM SupportTickets WHERE status = N'Новое'");
                    string authErrors = ExecuteScalarQuery(conn, "SELECT COUNT(*) FROM Users WHERE is_verified = 0");

                    string inSession = ExecuteScalarQuery(conn, "SELECT COUNT(DISTINCT equipment_id) FROM UsageLog WHERE end_date IS NULL");
                    string issues = ExecuteScalarQuery(conn, "SELECT COUNT(DISTINCT equipment_id) FROM SupportTickets WHERE status = N'Новое' AND equipment_id IS NOT NULL");

                    // Запрос на доступные: исключаем те, что в сессии ИЛИ имеют статус заявки 'Новое'
                    string availableQuery = @"
                SELECT COUNT(*) FROM Equipment 
                WHERE equipment_id NOT IN (SELECT equipment_id FROM UsageLog WHERE end_date IS NULL)
                  AND equipment_id NOT IN (SELECT equipment_id FROM SupportTickets WHERE status = N'Новое' AND equipment_id IS NOT NULL)";
                    string available = ExecuteScalarQuery(conn, availableQuery);

                    // ТРАССИРОВКА: Выводим реальные цифры из БД в консоль отладки Visual Studio 2022
                    System.Diagnostics.Debug.WriteLine($"\n[БД СТАТИСТИКА] Всего техники: {totalLaptops} | В сессии: {inSession} | Проблемные: {issues} | Доступно: {available}\n");

                    // Шаг 3: Безопасно заполняем интерфейс (если имя элемента не совпало в XAML — код просто пойдет дальше)
                    if (TxtMainTotalLaptops != null) TxtMainTotalLaptops.Text = totalLaptops;
                    if (TxtMainActiveStudents != null) TxtMainActiveStudents.Text = activeStudents;
                    if (TxtMainOpenRequests != null) TxtMainOpenRequests.Text = openRequests;
                    if (TxtMainAuthErrors != null) TxtMainAuthErrors.Text = authErrors;

                    if (TxtStatInSession != null) TxtStatInSession.Text = inSession;
                    else
                        System.Diagnostics.Debug.WriteLine("[ОШИБКА UI] TxtStatInSession равен NULL!");
                    if (TxtStatIssue != null) TxtStatIssue.Text = issues;
                    else
                        System.Diagnostics.Debug.WriteLine("[ОШИБКА UI] TxtMStatIssue равен NULL!");
                    if (TxtStatAvailable != null) TxtStatAvailable.Text = available;
                    else
                        System.Diagnostics.Debug.WriteLine("[ОШИБКА UI] TxtStatAvailable равен NULL!");

                    // Шаг 4: Обновляем списки таблиц и уведомлений
                    LoadActiveSessions(conn);
                    LoadLatestNotifications(conn);
                }
            }
            catch (Exception ex)
            {
                // Ловим сетевые падения или ошибки SQL
                System.Diagnostics.Debug.WriteLine($"[КРИТИЧЕСКАЯ ОШИБКА ОБНОВЛЕНИЯ]: {ex.Message}");
            }
        }

        #region ADO.NET SQL Запросы

        private string ExecuteScalarQuery(SqlConnection conn, string query)
        {
            try
            {
                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    object result = cmd.ExecuteScalar();
                    return result != null ? result.ToString() : "0";
                }
            }
            catch
            {
                return "0";
            }
        }

        private void LoadActiveSessions(SqlConnection conn)
        {
            var sessionsList = new List<object>();

            // Запрос с джоинами: собирает ФИО (Иванов И. И.), имя группы, бренд+модель ноута и кабинет
            string query = @"
                SELECT 
                    (u.last_name + ' ' + SUBSTRING(u.first_name, 1, 1) + '.' + ISNULL(' ' + SUBSTRING(u.middle_name, 1, 1) + '.', '')) AS Student,
                    g.group_name AS [Group],
                    (ISNULL(e.brand, '') + ' ' + ISNULL(e.model, '') + ' [' + e.inventory_number + ']') AS Laptop,
                    ISNULL(e.room, N'Не указан') AS Room
                FROM UsageLog log
                INNER JOIN Users u ON log.user_id = u.user_id
                INNER JOIN Groups g ON u.group_id = g.group_id
                INNER JOIN Equipment e ON log.equipment_id = e.equipment_id
                WHERE log.end_date IS NULL";

            using (SqlCommand cmd = new SqlCommand(query, conn))
            using (SqlDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    // Анонимный объект строго под привязки (Binding) в твоем XAML
                    sessionsList.Add(new
                    {
                        Student = reader["Student"].ToString(),
                        Group = reader["Group"].ToString(),
                        Laptop = reader["Laptop"].ToString(),
                        Room = reader["Room"].ToString()
                    });
                }
            }

            DgMainActiveSessions.ItemsSource = sessionsList;
        }

        private void LoadLatestNotifications(SqlConnection conn)
        {
            var notificationsList = new List<object>();

            // Добавляем ticket_id в SELECT
            string query = @"
        SELECT TOP 10 
            ticket_id,
            (title + N': ' + description) AS ContentText, 
            created_at AS Timestamp 
        FROM SupportTickets 
        WHERE status = N'Новое' 
        ORDER BY created_at DESC";

            using (SqlCommand cmd = new SqlCommand(query, conn))
            using (SqlDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    DateTime date = Convert.ToDateTime(reader["Timestamp"]);

                    // Добавляем ID в анонимный объект
                    notificationsList.Add(new
                    {
                        TicketId = Convert.ToInt32(reader["ticket_id"]),
                        ContentText = reader["ContentText"].ToString(),
                        Timestamp = date.ToString("HH:mm")
                    });
                }
            }

            LbMainNotifications.ItemsSource = notificationsList;
        }

        #endregion

        private void RefreshTables()
        {
            cbUserFilterGroup_SelectionChanged(null, null);
            cbRoomFilter_SelectionChanged(null, null);
        }
        private void LoadFilters()
        {
            using (SqlConnection conn = new SqlConnection(connString))
            {
                conn.Open();

                DataTable dtGroups = new DataTable();

                new SqlDataAdapter(
                    "SELECT 0 AS group_id, 'Все группы' AS group_name " +
                    "UNION ALL " +
                    "SELECT group_id, group_name FROM Groups",
                    conn).Fill(dtGroups);

                cbUserFilterGroup.ItemsSource = dtGroups.DefaultView;
                cbUserFilterGroup.SelectedValuePath = "group_id";
                cbUserFilterGroup.DisplayMemberPath = "group_name";

                DataTable dtRooms = new DataTable();

                new SqlDataAdapter(
                    "SELECT 'Все кабинеты' AS room " +
                    "UNION " +
                    "SELECT DISTINCT room FROM Equipment WHERE room IS NOT NULL",
                    conn).Fill(dtRooms);

                cbRoomFilter.ItemsSource = dtRooms.DefaultView;
                cbRoomFilter.SelectedValuePath = "room";
                cbRoomFilter.DisplayMemberPath = "room";
            }

            cbUserFilterGroup.SelectedIndex = 0;
            cbRoomFilter.SelectedIndex = 2;
        }

        public void LoadData()
        {
            using (SqlConnection conn = new SqlConnection(connString))
            {
                conn.Open();

                object selectedGroup = cbUserFilterGroup?.SelectedValue;
                object selectedRoom = cbRoomFilter?.SelectedValue;

                // 1. Загрузка Студентов
                string sqlUsers = @"SELECT u.user_id, u.last_name, u.first_name, g.group_name, u.student_card_number 
                            FROM Users u JOIN Groups g ON u.group_id = g.group_id";
                SqlDataAdapter daUsers = new SqlDataAdapter(sqlUsers, conn);
                DataTable dtUsers = new DataTable();
                daUsers.Fill(dtUsers);
                dgUsers.ItemsSource = dtUsers.DefaultView;

                // 2. Загрузка Техники
                string sqlEquip = @"SELECT e.equipment_id, et.name as type_name, e.model, e.inventory_number, e.machine_name, e.room
                            FROM Equipment e JOIN EquipmentTypes et ON e.type_id = et.type_id";
                SqlDataAdapter daEquip = new SqlDataAdapter(sqlEquip, conn);
                DataTable dtEquip = new DataTable();
                daEquip.Fill(dtEquip);
                dgEquipment.ItemsSource = dtEquip.DefaultView;

                SqlCommand cmdTypes = new SqlCommand("SELECT type_id, name FROM EquipmentTypes", conn);
                SqlDataAdapter daTy = new SqlDataAdapter(cmdTypes);
                DataTable dtTy = new DataTable();
                daTy.Fill(dtTy);
                cbEquipType.ItemsSource = dtTy.DefaultView;
                cbEquipType.SelectedValuePath = "type_id";

                if (selectedGroup != null)
                    cbUserFilterGroup.SelectedValue = selectedGroup;

                if (selectedRoom != null)
                    cbRoomFilter.SelectedValue = selectedRoom;
            }
        }
        private void AddUser_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(txtUserLN.Text))
                return;

            if (cbUserFilterGroup.SelectedValue == null ||
                Convert.ToInt32(cbUserFilterGroup.SelectedValue) == 0)
            {
                MessageBox.Show("Для добавления студента выберите конкретную группу в фильтре.");
                return;
            }

            using (SqlConnection conn = new SqlConnection(connString))
            {
                string sql = @"
                INSERT INTO Users
                (
                    last_name,
                    first_name,
                    course,
                    group_id,
                    student_card_number,
                    is_verified
                )
                VALUES
                (
                    @ln,
                    @fn,
                    1,
                    @gid,
                    @card,
                    1
                )";
                SqlCommand cmd = new SqlCommand(sql, conn);

                cmd.Parameters.AddWithValue("@ln", txtUserLN.Text);
                cmd.Parameters.AddWithValue("@fn", txtUserFN.Text);

                DataRowView row = (DataRowView)cbUserFilterGroup.SelectedItem;
                int groupId = Convert.ToInt32(row["group_id"]);
                cmd.Parameters.AddWithValue("@gid", groupId);

                cmd.Parameters.AddWithValue("@card", txtStudentCard.Text);

                conn.Open();
                cmd.ExecuteNonQuery();
            }
            txtUserLN.Clear();
            txtUserFN.Clear();
            txtStudentCard.Clear();
            LoadData();
            RefreshTables();
        }

        private void AddEquip_Click(object sender, RoutedEventArgs e)
        {
            if (cbEquipType.SelectedValue == null || string.IsNullOrEmpty(txtEquipInv.Text)) return;

            if (cbRoomFilter.SelectedValue == null ||
                cbRoomFilter.SelectedValue.ToString() == "Все кабинеты")
            {
                MessageBox.Show("Для добавления техники выберите конкретный кабинет в фильтре.");
                return;
            }

            using (SqlConnection conn = new SqlConnection(connString))
            {
                string sql = @"
                INSERT INTO Equipment
                (
                    type_id,
                    model,
                    inventory_number,
                    machine_name,
                    room
                )
                VALUES
                (
                    @tid,
                    @model,
                    @inv,
                    @machine,
                    @room
                )";
                SqlCommand cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@tid", cbEquipType.SelectedValue);
                cmd.Parameters.AddWithValue("@model", txtEquipModel.Text);
                cmd.Parameters.AddWithValue("@inv", txtEquipInv.Text);
                cmd.Parameters.AddWithValue("@room", cbRoomFilter.Text);
                cmd.Parameters.AddWithValue("@machine", txtMachineName.Text);

                conn.Open();
                cmd.ExecuteNonQuery();
            }
            txtEquipModel.Clear(); txtEquipInv.Clear(); txtMachineName.Clear();
            LoadData();
            RefreshTables();
        }
        private void DeleteUser_Click(object sender, RoutedEventArgs e)
        {
            if (dgUsers.SelectedItem == null) return;

            var row = (DataRowView)dgUsers.SelectedItem;
            int id = Convert.ToInt32(row["user_id"]);

            if (MessageBox.Show($"Удалить студента {row["last_name"]}?", "Внимание", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                try
                {
                    using (SqlConnection conn = new SqlConnection(connString))
                    {
                        conn.Open();
                        var cmd = new SqlCommand("DELETE FROM Users WHERE user_id = @id", conn);
                        cmd.Parameters.AddWithValue("@id", id);
                        cmd.ExecuteNonQuery();
                    }
                    LoadData();
                    RefreshTables();
                }
                catch (SqlException)
                {
                    MessageBox.Show("Не удалось удалить студента, так как за ним числятся записи в журнале учета!");
                }
            }
        }

        private void DeleteEquip_Click(object sender, RoutedEventArgs e)
        {
            if (dgEquipment.SelectedItem == null) return;
            var row = (DataRowView)dgEquipment.SelectedItem;
            int id = (int)row["equipment_id"];

            using (SqlConnection conn = new SqlConnection(connString))
            {
                conn.Open();
                var cmd = new SqlCommand("DELETE FROM Equipment WHERE equipment_id = @id", conn);
                cmd.Parameters.AddWithValue("@id", id);
                try { cmd.ExecuteNonQuery(); }
                catch { MessageBox.Show("Нельзя удалить: техника используется в журнале!"); }
            }
            LoadData();
            RefreshTables();
        }
        private void cbUserFilterGroup_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbUserFilterGroup.SelectedValue == null)
                return;

            string sql = @"
            SELECT
                u.user_id,
                u.last_name,
                u.first_name,
                g.group_name,
                u.student_card_number
            FROM Users u
            JOIN Groups g ON u.group_id = g.group_id";

            if (Convert.ToInt32(cbUserFilterGroup.SelectedValue) != 0)
            {
                sql += $" WHERE u.group_id = {cbUserFilterGroup.SelectedValue}";
            }

            using (SqlConnection conn = new SqlConnection(connString))
            {
                DataTable dt = new DataTable();
                new SqlDataAdapter(sql, conn).Fill(dt);
                dgUsers.ItemsSource = dt.DefaultView;
            }
        }

        private void cbRoomFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbRoomFilter.SelectedValue == null)
                return;

            string sql = @"
            SELECT
                e.equipment_id,
                et.name as type_name,
                e.model,
                e.inventory_number,
                e.machine_name,
                e.room
            FROM Equipment e
            JOIN EquipmentTypes et ON e.type_id = et.type_id";

            if (cbRoomFilter.SelectedValue.ToString() != "Все кабинеты")
            {
                sql += " WHERE e.room = @room";
            }

            using (SqlConnection conn = new SqlConnection(connString))
            {
                SqlCommand cmd = new SqlCommand(sql, conn);

                if (cbRoomFilter.SelectedValue.ToString() != "Все кабинеты")
                    cmd.Parameters.AddWithValue("@room", cbRoomFilter.SelectedValue);

                DataTable dt = new DataTable();

                SqlDataAdapter da = new SqlDataAdapter(cmd);
                da.Fill(dt);

                dgEquipment.ItemsSource = dt.DefaultView;
            }
        }

        private void LbMainNotifications_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 1. Проверяем, что элемент действительно выбран
            if (LbMainNotifications.SelectedItem == null)
                return;

            try
            {
                // 2. Приводим к dynamic, чтобы вытащить TicketId из анонимного объекта
                dynamic selectedItem = LbMainNotifications.SelectedItem;
                int ticketId = selectedItem.TicketId;

                // 3. Переключаем вкладку твоего главного TabControl на ту, где лежат уведомления.
                // ПОМЕНЯЙ "MainTabControl" на реальное имя (x:Name) твоего TabControl из XAML.
                // ПОМЕНЯЙ индекс "2" на индекс вкладки с уведомлениями (индексы идут с 0).
                if (MainTabControl != null)
                {
                    MainTabControl.SelectedIndex = 3;
                }

                // 4. Вызываем метод поиска и фокуса внутри самого контрола уведомлений.
                // ПОМЕНЯЙ "NotificationsTab" на реальное имя (x:Name) твоего NotificationsControl из XAML.
                if (MyNotificationsControl != null)
                {
                    MyNotificationsControl.SelectTicketById(ticketId);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ОШИБКА КЛИКА УВЕДОМЛЕНИЯ]: {ex.Message}");
            }
            finally
            {
                // 5. Сбрасываем выделение, чтобы по этому же уведомлению можно было кликнуть повторно
                LbMainNotifications.SelectedItem = null;
            }
        }
        // Этот метод должен быть в MainWindow.xaml.cs
        private void MyNotificationsControl_CreateStudentRequested(RequestTicket selected)
        {
            // 1. Переключаемся на вкладку со студентами (допустим, индекс 1)
            MainTabControl.SelectedIndex = 1;

            // 2. Парсим ФИО прямо из сырой строки тикета
            string[] names = (selected.StudentNameRaw ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // 3. Предзаполняем поля
            txtUserLN.Text = names.Length > 0 ? names[0] : "";
            txtUserFN.Text = names.Length > 1 ? names[1] : "";

            // Телеграм юзернейм или ID пихаем в поле студака/тг (куда тебе нужно)
            txtStudentCard.Text = selected.TelegramRaw ?? "";

            // 4. Сбрасываем группу, чтобы админ сам ее выбрал
            cbUserFilterGroup.SelectedItem = null;

            MessageBox.Show("Основа перенесена! Заполните оставшиеся поля (Группу) и нажмите 'Добавить студента'.", "Создание записи", MessageBoxButton.OK, MessageBoxImage.Information);
        }


        private void SwitchMode_Click(object sender, RoutedEventArgs e)
        {
            _isGroupMode = !_isGroupMode;

            if (_isGroupMode)
            {
                // Включаем режим добавления группы
                pnlStudentControls.Visibility = Visibility.Collapsed;
                pnlGroupControls.Visibility = Visibility.Visible;
                btnSwitchMode.Content = "➕ Режим: Добавить группу";
                btnSwitchMode.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6C757D"));

                // Блокируем фильтр групп, чтобы не мешался
                cbUserFilterGroup.IsEnabled = false;

                LoadCurators(); // Метод загрузки кураторов
            }
            else
            {
                // Включаем режим добавления студента
                pnlStudentControls.Visibility = Visibility.Visible;
                pnlGroupControls.Visibility = Visibility.Collapsed;
                btnSwitchMode.Content = "➕ Режим: Добавить студента";
                btnSwitchMode.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4E73DF"));

                cbUserFilterGroup.IsEnabled = true;
            }
        }
        private void LoadCurators()
        {
            using var conn = new SqlConnection(connString);
            var dt = new DataTable();
            // Грузим кураторов в комбобокс
            new SqlDataAdapter("SELECT curator_id, (last_name + ' ' + first_name) as full_name FROM Curators", conn).Fill(dt);
            cbCuratorSelector.ItemsSource = dt.DefaultView;
            cbCuratorSelector.SelectedValuePath = "curator_id";
        }
        private void SaveGroup_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtNewGroupName.Text) || cbCuratorSelector.SelectedValue == null)
            {
                MessageBox.Show("Введите название и выберите куратора!");
                return;
            }

            using (var conn = new SqlConnection(connString))
            {
                conn.Open();
                var cmd = new SqlCommand("INSERT INTO Groups (group_name, curator_id) VALUES (@name, @c_id)", conn);
                cmd.Parameters.AddWithValue("@name", txtNewGroupName.Text);
                cmd.Parameters.AddWithValue("@c_id", cbCuratorSelector.SelectedValue);
                cmd.ExecuteNonQuery();
            }

            MessageBox.Show("Группа успешно создана!");

            // Очищаем и возвращаемся в режим студентов
            txtNewGroupName.Clear();
            LoadFilters(); // Вызов твоего метода для обновления списка групп в фильтре
            SwitchMode_Click(null, null); // Авто-переключение обратно
        }
        private void DeleteGroup_Click(object sender, RoutedEventArgs e)
        {
            if (cbUserFilterGroup.SelectedValue == null)
            {
                MessageBox.Show("Сначала выберите группу в списке сверху!");
                return;
            }

            var result = MessageBox.Show("Удалить выбранную группу? Это действие необратимо (если в группе есть студенты, удаление может быть заблокировано базой данных).",
                                         "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    using (var conn = new SqlConnection(connString))
                    {
                        conn.Open();
                        // SQL-запрос на удаление
                        var cmd = new SqlCommand("DELETE FROM Groups WHERE group_id = @id", conn);
                        cmd.Parameters.AddWithValue("@id", cbUserFilterGroup.SelectedValue);
                        cmd.ExecuteNonQuery();
                    }

                    MessageBox.Show("Группа удалена.");
                    LoadFilters(); // Перезагружаем список групп в ComboBox
                }
                catch (SqlException ex)
                {
                    MessageBox.Show($"Ошибка удаления (возможно, в группе есть студенты): {ex.Message}");
                }
            }
        }
    }
}