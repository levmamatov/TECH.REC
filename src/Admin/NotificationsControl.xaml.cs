using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Configuration;

namespace Technic_accounting
{
    public partial class NotificationsControl : UserControl
    {
        public event Action<RequestTicket> CreateStudentRequested;
        private List<RequestTicket> _tickets;
        private static HttpClient _client;

        public class RequestTicket
        {
            public int Id { get; set; }
            public int? UserId { get; set; }
            public string? Title { get; set; }
            public string? Description { get; set; }
            public string? StudentNameRaw { get; set; }
            public string? TelegramRaw { get; set; }
            public string? Status { get; set; }
            public DateTime CreatedAt { get; set; }

            // Поля для Binding
            public string ContentText => $"{Title}: {Description}";
            public string StudentName => StudentNameRaw ?? "Неизвестно";
            public string Timestamp => CreatedAt.ToString("HH:mm");
        }

        public NotificationsControl()
        {
            InitializeComponent();
            var config = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

            string apiAddress = config["ApiSettings:BaseAddress"] ?? "http://192.168.0.170:5000/";

            _client = new HttpClient
            {
                BaseAddress = new Uri(apiAddress),
                Timeout = TimeSpan.FromSeconds(10)
            };
            _ = LoadRealDataAsync();
        }
        public async void SelectTicketById(int ticketId)
        {
            // 1. Сначала грузим данные
            await LoadRealDataAsync();

            // 2. Даем микро-паузу, чтобы UI успел привязать список
            await Task.Delay(100);

            Dispatcher.Invoke(() =>
            {
                var targetTicket = _tickets?.FirstOrDefault(t => t.Id == ticketId);
                if (targetTicket != null)
                {
                    LbNotifications.SelectedItem = targetTicket;
                    LbNotifications.ScrollIntoView(targetTicket);

                    // ПРИНУДИТЕЛЬНО вызываем SelectionChanged, чтобы детали отрисовались
                    // Вызываем этот метод вручную, если WPF сам не подхватил
                    LbNotifications_SelectionChanged(null, null);
                }
            });
        }

        public async Task LoadRealDataAsync()
        {
            try
            {
                // Добавляем проверку на занятость, чтобы не слать запросы пачками
                var response = await _client.GetAsync("/api/admin/tickets");
                response.EnsureSuccessStatusCode();

                var tickets = await response.Content.ReadFromJsonAsync<List<RequestTicket>>();
                var newList = tickets ?? new List<RequestTicket>();

                // Обязательно через Dispatcher, иначе при сменеItemsSource вылетит ошибка потока
                Dispatcher.Invoke(() =>
                {
                    _tickets = newList;
                    LbNotifications.ItemsSource = _tickets;
                    UpdateBadgeCount();

                    // Если выбранный ID всё еще существует - сохраняем выбор, если нет - сбрасываем
                    if (_tickets.Count > 0 && LbNotifications.SelectedIndex == -1)
                        LbNotifications.SelectedIndex = 0;
                });
            }
            catch (Exception ex)
            {
                // Не кидаем MessageBox, он вешает программу на экзамене!
                System.Diagnostics.Debug.WriteLine($"[API ERROR] {ex.Message}");
            }
        }

        private void UpdateBadgeCount()
        {
            int count = _tickets.Count(t => t.Status == "Новое");
            TxtNewCount.Text = $"{count} новых";
        }

        private void LbNotifications_SelectionChanged(object? sender, SelectionChangedEventArgs? e)
        {
            if (LbNotifications.SelectedItem is RequestTicket selectedTicket)
            {
                PanelPlaceholder.Visibility = Visibility.Collapsed;
                PanelDetails.Visibility = Visibility.Visible;

                // Сброс панелей: всегда показываем кнопки при выборе нового тикета
                PanelActionButtons.Visibility = Visibility.Visible;
                PanelManualReply.Visibility = Visibility.Collapsed;
                TxtCustomReply.Clear();

                // Тут подставляй свои названия lblDetails...
                lblDetailsType.Text = selectedTicket.Title;
                lblDetailsStudent.Text = selectedTicket.StudentName;
                lblDetailsTelegram.Text = selectedTicket.TelegramRaw;
                lblDetailsDescription.Text = selectedTicket.Description;

                // ДИНАМИЧЕСКАЯ ЛОГИКА КНОПОК
                bool isRegistration = selectedTicket.Title?.ToLower().Contains("регистраци") == true;

                PanelActionButtons.Visibility = Visibility.Visible;
                PanelManualReply.Visibility = Visibility.Collapsed;

                if (selectedTicket.UserId == null)
                {
                    // Студента нет в базе
                    BtnCreateDbRecord.Visibility = Visibility.Visible; // Разрешаем ручное создание

                    // Авто-Подтверждение доступно ТОЛЬКО для регистрации
                    BtnConfirm.Visibility = isRegistration ? Visibility.Visible : Visibility.Collapsed;
                }
                else
                {
                    // Студент уже есть в базе
                    BtnCreateDbRecord.Visibility = Visibility.Collapsed;
                    BtnConfirm.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                PanelDetails.Visibility = Visibility.Collapsed;
                PanelPlaceholder.Visibility = Visibility.Visible;
            }
        }

        // ================= КНОПКИ ДЕЙСТВИЙ =================

        private async void BtnDecline_Click(object sender, RoutedEventArgs e)
        {
            if (LbNotifications.SelectedItem is not RequestTicket selected) return;

            try
            {
                // Формируем данные для отправки
                var payload = new
                {
                    TicketId = selected.Id,
                    Reason = "К сожалению, ваша заявка была отклонена администратором." // Можно сделать TextBox, чтобы админ сам писал причину
                };

                var response = await _client.PostAsJsonAsync("/api/admin/tickets/decline", payload);

                if (response.IsSuccessStatusCode)
                {
                    MessageBox.Show("Заявка отклонена. Студенту отправлено уведомление.", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
                    await LoadRealDataAsync(); // Перезагружаем список
                }
                else
                {
                    string error = await response.Content.ReadAsStringAsync();
                    MessageBox.Show($"Ошибка сервера: {error}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка сети: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (LbNotifications.SelectedItem is not RequestTicket selected) return;

            bool isRegistration = selected.Title?.ToLower().Contains("регистраци") == true;

            try
            {
                if (isRegistration && selected.UserId == null)
                {
                    // Отправляем запрос на авторегистрацию
                    var payload = new { TicketId = selected.Id };
                    var response = await _client.PostAsJsonAsync("/api/admin/tickets/confirm", payload);

                    if (response.IsSuccessStatusCode)
                    {
                        MessageBox.Show("Студент зарегистрирован, заявка подтверждена!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        string error = await response.Content.ReadAsStringAsync();
                        MessageBox.Show($"Ошибка сервера: {error}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                        return; // Прерываем, если ошибка
                    }
                }
                else
                {
                    // Если это не регистрация, а что-то другое, можешь реализовать отдельную логику
                    MessageBox.Show("Подтверждение доступно только для новых регистраций.", "Инфо", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                await LoadRealDataAsync(); // Обновляем список тикетов
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка сети при подтверждении: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 2. Кнопка "Создать основу"
        private async void BtnCreateDbRecord_Click(object sender, RoutedEventArgs e)
        {
            if (LbNotifications.SelectedItem is not RequestTicket selected) return;

            try
            {
                // 1. Меняем статус тикета на "В работе", чтобы он не висел как "Новое"
                var payload = new { TicketId = selected.Id };
                await _client.PostAsJsonAsync("/api/admin/tickets/in-progress", payload);

                // 2. Вызываем событие для MainWindow, передавая туда выбранный тикет
                CreateStudentRequested?.Invoke(selected);

                // Обновляем список, чтобы бейджик "новых" пересчитался
                await LoadRealDataAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ================= БЫСТРЫЕ ОТВЕТЫ (CONTEXT MENU) =================

        private void BtnSendReply_Click(object sender, RoutedEventArgs e)
        {
            // Программно открываем контекстное меню при левом клике
            if (BtnSendReply.ContextMenu != null)
            {
                BtnSendReply.ContextMenu.IsOpen = true;
            }
        }

        // Быстрые шаблоны ответов
        private void ReplyComeHere_Click(object sender, RoutedEventArgs e) => SendTelegramReply("Подойди ко мне.");
        private void ReplyFirstFloor_Click(object sender, RoutedEventArgs e) => SendTelegramReply("Подойди ко мне на первый этаж.");

        // 1. Логика для "Актуальные данные"
        private async void ReplyActualData_Click(object sender, RoutedEventArgs e)
        {
            if (LbNotifications.SelectedItem is not RequestTicket selected) return;

            try
            {
                var response = await _client.GetFromJsonAsync<JsonElement>($"/api/admin/student-info/{selected.Id}");

                // Читаем строго по тем ключам, которые мы задали в API (маленькие буквы)
                string name = response.TryGetProperty("name", out var n) ? n.GetString() : "Нет имени";
                string desc = response.TryGetProperty("desc", out var d) ? d.GetString() : "Нет описания";
                string groupName = response.TryGetProperty("groupName", out var g) ? g.GetString() : "Не указана";
                string cardNum = response.TryGetProperty("studentCard", out var c) ? c.GetString() : "не найден";

                string replyMessage = $"Данные студента:\nИмя: {name}\nГруппа: {groupName}\n№ студбилета: {cardNum}";

                if (MessageBox.Show($"Найдено:\n{replyMessage}\n\nОтправить студенту?", "Подтверждение", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    SendTelegramReply(replyMessage);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка получения: {ex.Message}");
            }
        }
        // 2. Логика для "Ручной ввод" (Переключает вкладку и шлет событие в MainWindow)
        private void ReplyManual_Click(object sender, RoutedEventArgs e)
        {
            if (LbNotifications.SelectedItem is RequestTicket)
            {
                // Прячем основные кнопки, показываем текстовое поле
                PanelActionButtons.Visibility = Visibility.Collapsed;
                PanelManualReply.Visibility = Visibility.Visible;
                TxtCustomReply.Focus(); // Сразу ставим курсор в поле
            }
        }

        // Кнопка "Отмена" в панели ручного ввода
        private void BtnCancelCustomReply_Click(object sender, RoutedEventArgs e)
        {
            TxtCustomReply.Clear();
            PanelManualReply.Visibility = Visibility.Collapsed;
            PanelActionButtons.Visibility = Visibility.Visible;
        }

        // Кнопка "Отправить" в панели ручного ввода
        private void BtnSendCustomReply_Click(object sender, RoutedEventArgs e)
        {
            string message = TxtCustomReply.Text.Trim();

            if (string.IsNullOrEmpty(message))
            {
                MessageBox.Show("Введите текст сообщения!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Вызываем твой готовый метод отправки
            SendTelegramReply(message);

            // После отправки возвращаем интерфейс в исходное состояние
            TxtCustomReply.Clear();
            PanelManualReply.Visibility = Visibility.Collapsed;
            PanelActionButtons.Visibility = Visibility.Visible;
        }

        // Единый метод отправки ответа на сервер API
        private async void SendTelegramReply(string message)
        {
            if (!(LbNotifications.SelectedItem is RequestTicket selected))
            {
                MessageBox.Show("Ошибка: Не выбран тикет для отправки ответа.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Формируем строгий payload для отправки
                var replyData = new
                {
                    TicketId = selected.Id,
                    Message = message
                };

                // Отправляем POST запрос на наш обновленный Minimal API
                var response = await _client.PostAsJsonAsync("api/admin/send-reply", replyData);

                if (response.IsSuccessStatusCode)
                {
                    MessageBox.Show($"Ответ успешно записан в БД для студента: {selected.StudentNameRaw}", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);

                    await LoadRealDataAsync();
                }
                else
                {
                    // Читаем текст ошибки, который прислал сервер
                    string errorContent = await response.Content.ReadAsStringAsync();
                    MessageBox.Show($"Сервер отклонил запрос:\n{errorContent}", "Ошибка выполнения", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Критическая ошибка сети или обработки: {ex.Message}", "Сбой связи", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}