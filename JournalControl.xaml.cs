using System.Data;
using System.Data.SqlClient;
using System.Windows;
using System.Windows.Controls;
using Excel = Microsoft.Office.Interop.Excel;
using Microsoft.Extensions.Configuration;

namespace Technic_accounting
{
    public partial class JournalControl : UserControl
    {
        private string _filePath;
        private DataTable _dt;
        private bool _exportMode = false;
        private readonly string connString;

        public JournalControl()
        {
            InitializeComponent();
            var config = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

            connString = config.GetConnectionString("DefaultConnection")
                ?? @"Server=26.197.58.130,1433; Database=CourseworkPractice; User ID=sa; Password=12345; TrustServerCertificate=True;";

            LoadFilters();
            LoadJournalData();
        }

        private void LoadFilters()
        {
            try
            {
                using (SqlConnection conn = new SqlConnection(connString))
                {
                    conn.Open();

                    // 1. СТУДЕНТЫ + Опция "Все"
                    var cmdUser = new SqlCommand("SELECT user_id, last_name + ' ' + first_name AS FIO FROM Users", conn);
                    var readerUser = cmdUser.ExecuteReader();
                    var users = new List<object> { new { user_id = (int?)null, FullInfo = "— Все студенты —" } };
                    while (readerUser.Read())
                    {
                        users.Add(new { user_id = Convert.ToInt32(readerUser["user_id"]), FullInfo = readerUser["FIO"].ToString() });
                    }
                    cbStudent.ItemsSource = users;
                    cbStudent.SelectedIndex = 0;
                    readerUser.Close();

                    // 2. ГРУППЫ + Опция "Все"
                    var cmdGroup = new SqlCommand("SELECT group_id, group_name FROM Groups", conn);
                    var readerGroup = cmdGroup.ExecuteReader();
                    var groups = new List<object> { new { group_id = (int?)null, FullInfo = "— Все группы —" } };
                    while (readerGroup.Read())
                    {
                        groups.Add(new { group_id = Convert.ToInt32(readerGroup["group_id"]), FullInfo = readerGroup["group_name"].ToString() });
                    }
                    cbJournalGroup.ItemsSource = groups;
                    cbJournalGroup.SelectedIndex = 0;
                    readerGroup.Close();

                    // 3. ОБОРУДОВАНИЕ + Опция "Все"
                    var cmdEq = new SqlCommand("SELECT equipment_id, brand + ' ' + model AS Device FROM Equipment", conn);
                    var readerEq = cmdEq.ExecuteReader();
                    var devices = new List<object> { new { equipment_id = (int?)null, FullInfo = "— Все оборудование —" } };
                    while (readerEq.Read())
                    {
                        devices.Add(new { equipment_id = Convert.ToInt32(readerEq["equipment_id"]), FullInfo = readerEq["Device"].ToString() });
                    }
                    cbEquipment.ItemsSource = devices;
                    cbEquipment.SelectedIndex = 0;
                    readerEq.Close();

                    // 4. КАБИНЕТЫ + Опция "Все" 
                    var cmdRoom = new SqlCommand("SELECT DISTINCT room AS room_number FROM Equipment", conn);
                    var readerRoom = cmdRoom.ExecuteReader();
                    var rooms = new List<object> { new { room_id = (int?)null, FullInfo = "— Все кабинеты —" } };
                    while (readerRoom.Read())
                    {
                        rooms.Add(new { FullInfo = readerRoom["room_number"].ToString() });
                    }
                    cbJournalRoom.ItemsSource = rooms;
                    cbJournalRoom.SelectedIndex = 0;
                    readerRoom.Close();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка загрузки фильтров: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadJournalData()
        {
            try
            {
                string query = @"SELECT
                                    et.name AS [Тип техники],
                                    e.inventory_number AS [Инвентарный номер],
                                    (u.last_name + ' ' + u.first_name) AS [ФИО],
                                    g.group_name AS [Группа],
                                    CONVERT(varchar(5), ul.start_date, 108) AS [Начало],
                                    CONVERT(varchar(5), ul.end_date, 108) AS [Конец],
                                    CONVERT(varchar(10), ul.start_date, 104) AS [Дата]
                                 FROM UsageLog ul
                                 JOIN Users u ON ul.user_id = u.user_id
                                 JOIN Groups g ON u.group_id = g.group_id
                                 JOIN Equipment e ON ul.equipment_id = e.equipment_id
                                 JOIN EquipmentTypes et ON e.type_id = et.type_id
                                 WHERE 1=1";

                // Если глобальная галочка "Выгрузить все" НЕ стоит, применяем выборочные фильтры
                if (chbAll.IsChecked != true)
                {
                    if (cbStudent.SelectedValue != null)
                        query += $" AND u.user_id = {cbStudent.SelectedValue}";

                    if (cbJournalGroup.SelectedValue != null)
                        query += $" AND u.group_id = {cbJournalGroup.SelectedValue}";

                    if (cbEquipment.SelectedValue != null)
                        query += $" AND e.equipment_id = {cbEquipment.SelectedValue}";

                    // Фильтр кабинета (Связан через оборудование, измени e.room_id если логика иная)
                    if (cbJournalRoom.SelectedValue != null)
                        query += $" AND e.room_id = {cbJournalRoom.SelectedValue}";

                    if (dpDate.SelectedDate != null)
                        query += $" AND CAST(ul.start_date AS DATE) = '{dpDate.SelectedDate.Value:yyyyMMdd}'";

                    // Фильтрация по Статусу Сессии (ComboBoxItem)
                    if (cbJournalSessionStatus.SelectedIndex == 1) // Активные
                        query += " AND ul.end_date IS NULL";
                    else if (cbJournalSessionStatus.SelectedIndex == 2) // Закрытые
                        query += " AND ul.end_date IS NOT NULL";

                    // Проблемные записи
                    if (chbJournalProblems?.IsChecked == true)
                        query += " AND ul.end_date IS NULL AND ul.start_date < DATEADD(hour, -4, GETDATE())"; // Пример логики "зависло"
                }

                DataTable dt = new DataTable();
                using (SqlConnection conn = new SqlConnection(connString))
                {
                    SqlDataAdapter da = new SqlDataAdapter(query, conn);
                    da.Fill(dt);
                }

                _dt = dt;
                dgPreview.ItemsSource = _dt.DefaultView;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при формировании журнала: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Generate_Click(object sender, RoutedEventArgs e) => LoadJournalData();

        private void ImportExcel_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Excel Worksheets|*.xlsx;*.xls" };
            if (dlg.ShowDialog() == true)
            {
                _filePath = dlg.FileName;
                _exportMode = false;
                try
                {
                    LoadExcelData();
                    PanelImportConfirmation.Visibility = Visibility.Visible;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка чтения Excel: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void LoadExcelData()
        {
            var xlApp = new Excel.Application();
            Excel.Workbook xlWorkbook = xlApp.Workbooks.Open(_filePath);
            Excel._Worksheet xlWorksheet = (Excel.Worksheet)xlWorkbook.Sheets[1];
            Excel.Range xlRange = xlWorksheet.UsedRange;

            _dt = new DataTable();
            _dt.Columns.Add("Тип техники");
            _dt.Columns.Add("Инвентарный номер");
            _dt.Columns.Add("ФИО");
            _dt.Columns.Add("Группа");
            _dt.Columns.Add("Начало");
            _dt.Columns.Add("Конец");
            _dt.Columns.Add("Дата");

            for (int i = 2; i <= xlRange.Rows.Count; i++)
            {
                string firstCell = (xlRange.Cells[i, 1] as Excel.Range).Text.ToString();
                if (string.IsNullOrWhiteSpace(firstCell)) continue;

                DataRow dr = _dt.NewRow();
                for (int j = 1; j <= 7; j++)
                    dr[j - 1] = (xlRange.Cells[i, j] as Excel.Range).Text.ToString();
                _dt.Rows.Add(dr);
            }

            dgPreview.ItemsSource = _dt.DefaultView;
            xlWorkbook.Close(false);
            xlApp.Quit();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_exportMode) ExportToExcel(_dt);
                else SaveToDatabase(_dt);

                PanelImportConfirmation.Visibility = Visibility.Collapsed;
                LoadJournalData();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            PanelImportConfirmation.Visibility = Visibility.Collapsed;
            LoadJournalData();
        }

        private void ExportReport_Click(object sender, RoutedEventArgs e)
        {
            if (_dt == null || _dt.Rows.Count == 0)
            {
                MessageBox.Show("Нет данных для экспорта!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try { ExportToExcel(_dt); }
            catch (Exception ex) { MessageBox.Show($"Ошибка экспорта: {ex.Message}"); }
        }

        private void CloseSession_Click(object sender, RoutedEventArgs e)
        {
            if (dgPreview.SelectedItem is DataRowView selectedRow)
            {
                try
                {
                    using (SqlConnection conn = new SqlConnection(connString))
                    {
                        conn.Open();
                        string query = @"UPDATE ul SET ul.end_date = GETDATE()
                                         FROM UsageLog ul
                                         JOIN Users u ON ul.user_id = u.user_id
                                         JOIN Equipment e ON ul.equipment_id = e.equipment_id
                                         WHERE e.inventory_number = @inv AND (u.last_name + ' ' + u.first_name) = @fio AND ul.end_date IS NULL";

                        SqlCommand cmd = new SqlCommand(query, conn);
                        cmd.Parameters.AddWithValue("@inv", selectedRow["Инвентарный номер"].ToString());
                        cmd.Parameters.AddWithValue("@fio", selectedRow["ФИО"].ToString());

                        if (cmd.ExecuteNonQuery() > 0) LoadJournalData();
                        else MessageBox.Show("Сессия уже закрыта.");
                    }
                }
                catch (Exception ex) { MessageBox.Show(ex.Message); }
            }
        }

        private void SaveToDatabase(DataTable dt)
        {
            using (SqlConnection conn = new SqlConnection(connString))
            {
                conn.Open();
                foreach (DataRow row in dt.Rows)
                {
                    string fullFIO = row["ФИО"].ToString();
                    if (string.IsNullOrWhiteSpace(fullFIO)) continue;

                    string query = @"
                        DECLARE @uID int = (SELECT TOP 1 user_id FROM Users WHERE last_name = @ln);
                        DECLARE @eID int = (SELECT TOP 1 equipment_id FROM Equipment WHERE inventory_number = @inv);
                        IF (@uID IS NOT NULL AND @eID IS NOT NULL)
                        BEGIN
                            INSERT INTO UsageLog (user_id, equipment_id, start_date, end_date) VALUES (@uID, @eID, @start, @end);
                        END";

                    using (SqlCommand cmd = new SqlCommand(query, conn))
                    {
                        cmd.Parameters.AddWithValue("@ln", fullFIO.Split(' ')[0]);
                        cmd.Parameters.AddWithValue("@inv", row["Инвентарный номер"].ToString());
                        try
                        {
                            DateTime date = DateTime.Parse(row["Дата"].ToString());
                            TimeSpan startTime = TimeSpan.Parse(row["Начало"].ToString().Replace('.', ':'));
                            TimeSpan endTime = TimeSpan.Parse(row["Конец"].ToString().Replace('.', ':'));
                            cmd.Parameters.AddWithValue("@start", date.Add(startTime));
                            cmd.Parameters.AddWithValue("@end", date.Add(endTime));
                            cmd.ExecuteNonQuery();
                        }
                        catch { /* Пропуск ошибок строк */ }
                    }
                }
            }
        }

        private void ExportToExcel(DataTable dt)
        {
            var xlApp = new Excel.Application { Visible = true };
            var wb = xlApp.Workbooks.Add();
            var sheet = (Excel.Worksheet)wb.Sheets[1];
            sheet.Cells[1, 1] = "ЖУРНАЛ УЧЕТА КОМПЬЮТЕРНОЙ ТЕХНИКИ";
            sheet.Range[sheet.Cells[1, 1], sheet.Cells[1, dt.Columns.Count]].Merge();
            sheet.Cells[1, 1].Font.Bold = true;

            for (int i = 0; i < dt.Columns.Count; i++) sheet.Cells[3, i + 1] = dt.Columns[i].ColumnName;
            for (int row = 0; row < dt.Rows.Count; row++)
                for (int col = 0; col < dt.Columns.Count; col++)
                    sheet.Cells[row + 4, col + 1] = dt.Rows[row][col]?.ToString();
            sheet.Columns.AutoFit();
        }

        private void chbAll_Checked(object sender, RoutedEventArgs e) => ToggleFilters(false);
        private void chbAll_Unchecked(object sender, RoutedEventArgs e) => ToggleFilters(true);

        private void ToggleFilters(bool isEnabled)
        {
            if (cbStudent != null) cbStudent.IsEnabled = cbJournalGroup.IsEnabled = cbEquipment.IsEnabled = cbJournalRoom.IsEnabled = dpDate.IsEnabled = cbJournalSessionStatus.IsEnabled = isEnabled;
        }
    }
}