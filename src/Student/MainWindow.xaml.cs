using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Technic_accounting_student.Data;
using Technic_accounting_student.Models;
using Technic_accounting_student.Services;
using System.Configuration;

namespace Technic_accounting_student
{
    public partial class MainWindow : Window
    {
        
        private static HttpClient _client;

        private int _currentLogId = 0;
        private bool _isAuthSuccess = false;
        private DispatcherTimer _messageTimer;
        private int _currentUserId = 0;
        private int _currentTicketId = 0;

        private readonly DbManager _db = new();
        private EquipmentItem? _currentEquipment;
        // Флаг-предохранитель от повторных кликов и двойных MessageBox
        private bool _isProcessingTicket = false;
        private bool _isCriticalShutdown = false;
        private DateTime _lastGroupsRefresh = DateTime.MinValue;

        public MainWindow()
        {
            string serverUrl = System.Configuration.ConfigurationManager.AppSettings["ServerUrl"]
                       ?? "http://localhost:5000/"; // Дефолт на всякий случай
            _client = new HttpClient
            {
                BaseAddress = new Uri(serverUrl),
                Timeout = TimeSpan.FromSeconds(3) // Задаем мелкий таймаут, чтобы приложение не висло при плохой сети
            };
            InitializeComponent();

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            this.PreviewKeyDown += MainWindow_PreviewKeyDown;

            // 1. ПЕРЕХВАТ ВЫКЛЮЧЕНИЯ ИЛИ ПЕРЕЗАГРУЗКИ ОС
            Application.Current.SessionEnding += Current_SessionEnding;

            // 2. ПЕРЕХВАТ УХОДА В СОН
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyboardDevice.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && e.Key == Key.X)
            {
                var result = MessageBox.Show("Экстренное завершение работы? Сессия будет сброшена.",
                    "Админ-режим", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    _isAuthSuccess = true;
                    if (_currentLogId > 0)
                    {
                        _ = CloseSessionOnApiAsync(_currentLogId);
                    }
                    Application.Current.Shutdown();
                }
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadGroups();
                DetectCurrentComputer();

                _messageTimer = new DispatcherTimer();
                _messageTimer.Interval = TimeSpan.FromSeconds(5);
                _messageTimer.Tick += async (s, ev) =>
                {
                    // ЖЕЛЕЗОБЕТОННАЯ ЗАЩИТА: Останавливаем таймер, чтобы избежать повторных тиков
                    // пока студент держит открытым MessageBox или тупит сеть
                    _messageTimer.Stop();

                    try
                    {
                        RefreshGroupsIfNeeded();
                        if (_currentUserId > 0)
                        {
                            var unreadReplies = await _client.GetFromJsonAsync<List<UnreadReplyDto>>($"/api/tickets/unread-replies/{_currentUserId}");
                            if (unreadReplies != null)
                            {
                                foreach (var reply in unreadReplies)
                                {
                                    // Сначала помечаем на сервере как прочитанное, чтобы база знала об этом
                                    await _client.PostAsync($"/api/tickets/mark-reply-read/{reply.ReplyId}", null);

                                    // И только потом показываем окно — теперь повторный тик его не подхватит
                                    MessageBox.Show(reply.Text, $"Ответ по заявке [{reply.TicketTitle}]", MessageBoxButton.OK, MessageBoxImage.Information);
                                }
                            }
                        }
                        else if (_currentTicketId > 0)
                        {
                            var statusInfo = await _client.GetFromJsonAsync<TicketStatusDto>($"/api/tickets/check-status/{_currentTicketId}");
                            if (statusInfo != null)
                            {
                                // ПОДСТРОЙКА ПОД НОВЫЕ СТАТУСЫ API И ИСПРАВЛЕНИЕ ДУБЛЕЙ:
                                // Сбрасываем ID тикета ДО вызова MessageBox.Show

                                if (statusInfo.Status == "Закрыто") // Наш новый статус успешной регистрации
                                {
                                    _currentTicketId = 0;
                                    MessageBox.Show("Ваша заявка на регистрацию успешно одобрена куратором!\nТеперь вы можете войти в систему под своим студенческим.", "Успешная регистрация", MessageBoxButton.OK, MessageBoxImage.Information);
                                }
                                else if (statusInfo.Status == "Отклонено")
                                {
                                    _currentTicketId = 0;
                                    MessageBox.Show($"Ваша заявка была отклонена куратором.\nПричина: {statusInfo.LastReply ?? "Не указана"}", "Заявка отклонена", MessageBoxButton.OK, MessageBoxImage.Warning);
                                }
                                else if (statusInfo.Status == "В работе") // Наш новый статус обычного ответа техподдержки
                                {
                                    _currentTicketId = 0;
                                    MessageBox.Show($"Получен ответ от администратора:\n\n{statusInfo.LastReply}", "Сообщение от поддержки", MessageBoxButton.OK, MessageBoxImage.Information);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Ошибка фонового обновления данных: {ex.Message}");
                    }
                    finally
                    {
                        // Запускаем таймер обратно только тогда, когда ВСЯ работа (включая клики юзера) завершена
                        _messageTimer.Start();
                    }
                };
                _messageTimer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка загрузки", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // =========================================================================
        // ЛОГИКА АВТОМАТИЧЕСКОГО ЗАКРЫТИЯ (ПЕРЕХВАТ СИСТЕМНЫХ СОБЫТИЙ)
        // =========================================================================

        // Срабатывает при выключении ПК, перезагрузке или разлогине из Windows
        private void Current_SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            if (_currentLogId > 0)
            {
                // чтобы заблокировать поток закрытия ОС на долю секунды и успеть отправить пакет.
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, $"api/auth/logout?logId={_currentLogId}");
                    _client.Send(request);
                }
                catch { /* Гасим ошибки, система всё равно завершает работу */ }
            }
        }

        // Срабатывает при закрытии крышки / уходе в сон
        private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend && _currentLogId > 0)
            {
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, $"api/auth/logout?logId={_currentLogId}");
                    _client.Send(request);
                }
                catch { }
            }
        }

        // Вспомогательный асинхронный метод для штатных ситуаций
        private async Task CloseSessionOnApiAsync(int logId)
        {
            try
            {
                await _client.PostAsJsonAsync($"api/auth/logout?logId={logId}", new { });
            }
            catch { }
        }

        // =========================================================================
        // ЖЕЛЕЗОБЕТОННЫЙ РУЧНОЙ ВАРИАНТ (КНОПКА НА ИНТЕРФЕЙСЕ)
        // =========================================================================
        // Навесь этот обработчик на твою кастомную кнопку «Выйти и выключить ПК» на SuccessPanel
        private async void btnExitAndShutdown_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Вы действительно хотите завершить сессию работы и выключить компьютер?",
                "Завершение работы", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                // 1. Показываем юзеру, что процесс пошел
                this.IsEnabled = false;

                // 2. Спокойно и штатно уведомляем сервер
                if (_currentLogId > 0)
                {
                    await CloseSessionOnApiAsync(_currentLogId);
                }

                // 3. Снимаем блокировку закрытия WPF окна
                _isAuthSuccess = true;

                // 4. Гасим компьютер через командную строку Windows (/s - выключить, /t 0 - незамедлительно)
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "shutdown",
                        Arguments = "/s /t 0",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
                catch
                {
                    // Если прав на выключение нет, просто закрываем программу
                    Application.Current.Shutdown();
                }
            }
        }

        // =========================================================================
        // АВТОРИЗАЦИЯ ЧЕРЕЗ API
        // =========================================================================
        private async void AuthorizeStudent()
        {
            if (string.IsNullOrWhiteSpace(txtLastName.Text) || string.IsNullOrWhiteSpace(txtFirstName.Text) ||
                cbGroup.SelectedItem == null || string.IsNullOrWhiteSpace(txtIdentifier.Text))
            {
                MessageBox.Show("Заполните все поля авторизации.", "Проверка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var loginData = new
            {
                StudentCard = txtIdentifier.Text.Trim(),
                MachineName = MachineHelper.GetCurrentMachineName()
            };

            try
            {
                var response = await _client.PostAsJsonAsync("api/auth/login", loginData);

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<LoginResultDto>();
                    if (result != null)
                    {
                        _currentLogId = result.LogId;
                        _currentUserId = (int)result.UserId;
                        _isAuthSuccess = true;
                        _currentTicketId = 0;

                        StudentGroup group = (StudentGroup)cbGroup.SelectedItem;

                        LoginPanel.Visibility = Visibility.Collapsed;
                        SuccessPanel.Visibility = Visibility.Visible;

                        this.Topmost = false;
                        this.WindowState = WindowState.Minimized;

                        txtSuccessStudent.Text = $"{txtLastName.Text.Trim()} {txtFirstName.Text.Trim()}";
                        txtSuccessGroup.Text = group.GroupName;
                        txtSuccessInventory.Text = _currentEquipment?.InventoryNumber ?? "-";
                        txtSuccessStart.Text = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
                    }
                }
                else
                {
                    var error = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
                    MessageBox.Show(error?.Message ?? "Ошибка авторизации.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка связи с сервером API: {ex.Message}", "Ошибка сети", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadGroups()
        {
            var currentText = cbGroup.Text;

            cbGroup.ItemsSource = _db.GetGroups();

            if (!string.IsNullOrWhiteSpace(currentText))
            {
                cbGroup.Text = currentText;
            }
        }
        private void RefreshGroupsIfNeeded()
        {
            try
            {
                // Не трогаем список после авторизации
                if (_isAuthSuccess)
                    return;

                // Обновляем раз в минуту
                if ((DateTime.Now - _lastGroupsRefresh).TotalSeconds < 60)
                    return;

                _lastGroupsRefresh = DateTime.Now;

                string selectedGroup = "";

                if (cbGroup.SelectedItem is StudentGroup selected)
                    selectedGroup = selected.GroupName;

                var groups = _db.GetGroups();

                cbGroup.ItemsSource = groups;

                if (!string.IsNullOrWhiteSpace(selectedGroup))
                {
                    foreach (var group in groups)
                    {
                        if (group.GroupName == selectedGroup)
                        {
                            cbGroup.SelectedItem = group;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка обновления групп: {ex.Message}");
            }
        }

        private void DetectCurrentComputer()
        {
            string machineName = MachineHelper.GetCurrentMachineName();
            _currentEquipment = _db.GetEquipmentByMachineName(machineName);

            if (_currentEquipment == null)
            {
                MessageBox.Show($"Компьютер '{machineName}' не зарегистрирован в базе данных.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                _isCriticalShutdown = true;
                Application.Current.Shutdown();
            }
        }

        private void rbMethod_Checked(object sender, RoutedEventArgs e)
        {
            if (lblIdentifierTitle == null || txtIdentifier == null) return;
            lblIdentifierTitle.Text = rbMethodCard.IsChecked == true ? "🪪 Номер студенческого билета" : "📱 Аккаунт Telegram (например, @username)";
        }

        private void btnLogin_Click(object sender, RoutedEventArgs e) => AuthorizeStudent();

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_isCriticalShutdown) return;
            if (!_isAuthSuccess)
            {
                e.Cancel = true;
                MessageBox.Show("Доступ к системе заблокирован до прохождения авторизации!", "Защита киоска", MessageBoxButton.OK, MessageBoxImage.Stop);
            }
            else
            {
                SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
            }
        }
        private void lblSupportLink_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Если запрос уже обрабатывается или открыто окно — полностью игнорируем клик
            if (_isProcessingTicket) return;

            _isProcessingTicket = true; // Выставляем блокировку

            var result = MessageBox.Show("Что случилось?\n\n[Да] — Нужна помощь.\n[Нет] — Я хочу зарегистрироваться.", "Поддержка", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                HandleSupportTicket("Проблема/Помощь");
            }
            else if (result == MessageBoxResult.No)
            {
                HandleSupportTicket("Регистрация");
            }
            else
            {
                // Если нажали "Отмена", снимаем блокировку
                _isProcessingTicket = false;
            }
        }

        private async void HandleSupportTicket(string type)
        {
            if (string.IsNullOrWhiteSpace(txtLastName.Text))
            {
                MessageBox.Show("Сначала укажите Фамилию и Имя.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                _isProcessingTicket = false; // Не забываем разблокировать
                return;
            }

            lblSupportLink.IsEnabled = false;

            var ticket = new SupportTicketDto
            {
                UserId = _currentUserId > 0 ? _currentUserId : null,
                Title = type,
                Description = type == "Регистрация"
                                    ? "Запрос на регистрацию"
                                    : "Проблема с оборудованием",

                StudentNameRaw = $"{txtLastName.Text} {txtFirstName.Text}".Trim(),

                TelegramRaw = string.IsNullOrWhiteSpace(txtIdentifier.Text)
                                ? null
                                : txtIdentifier.Text.Trim(),

                Status = "Новое"
            };

            try
            {
                var response = await _client.PostAsJsonAsync("api/auth/register-request", ticket);
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<RegisterResultDto>();
                    if (result != null) _currentTicketId = result.TicketId;

                    MessageBox.Show("Заявка отправлена куратору. Ожидайте ответа прямо на этом экране.", "Отправлено", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    // Получаем понятный текст ошибки от сервера, а не просто пустой мессадж
                    string errorContent = await response.Content.ReadAsStringAsync();
                    MessageBox.Show($"Ошибка сервера: {errorContent}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка сети: {ex.Message}", "Сбой связи", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // В самом конце освобождаем кнопку и флаг для следующих заявок
                lblSupportLink.IsEnabled = true;
                _isProcessingTicket = false;
            }
        }
    }

    public class LoginResultDto { public int LogId { get; set; } public int? UserId { get; set; } }
    public class ErrorResponseDto { public string Message { get; set; } }
    public class RegisterResultDto { public int TicketId { get; set; } public string Message { get; set; } }
    public class UnreadReplyDto { public int ReplyId { get; set; } public int TicketId { get; set; } public string Text { get; set; } public string TicketTitle { get; set; } }
    public class TicketStatusDto { public string Status { get; set; } public string? LastReply { get; set; } }
}