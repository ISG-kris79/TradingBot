using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot
{
    /// <summary>
    /// 관리자용 사용자 승인 관리 창
    /// </summary>
    public partial class UserManagementWindow : Window
    {
        private readonly DatabaseService _dbService;
        private readonly string _currentAdminUsername;

        public UserManagementWindow(string adminUsername)
        {
            InitializeComponent();
            _dbService = new DatabaseService();
            _currentAdminUsername = adminUsername;
            LoadUsers();
        }

        private async void LoadUsers()
        {
            try
            {
                btnRefresh.IsEnabled = false;
                txtStatus.Text = "사용자 목록 로딩 중...";

                var users = await _dbService.GetAllUsersAsync();
                
                dgUsers.ItemsSource = users;
                txtStatus.Text = $"총 {users.Count}명의 사용자 (승인 대기: {users.Count(u => !u.IsApproved)}명)";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"사용자 목록 로드 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                txtStatus.Text = "로드 실패";
            }
            finally
            {
                btnRefresh.IsEnabled = true;
            }
        }

        private async void btnApprove_Click(object sender, RoutedEventArgs e)
        {
            if (dgUsers.SelectedItem is User selectedUser)
            {
                if (selectedUser.IsApproved)
                {
                    MessageBox.Show("이미 승인된 사용자입니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var result = MessageBox.Show(
                    $"사용자 '{selectedUser.Username}'을(를) 승인하시겠습니까?\n\n이메일: {selectedUser.Email}",
                    "사용자 승인",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    bool success = await _dbService.ApproveUserAsync(selectedUser.Id, _currentAdminUsername);
                    
                    if (success)
                    {
                        MessageBox.Show($"사용자 '{selectedUser.Username}'이(가) 승인되었습니다.", "승인 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                        LoadUsers(); // 목록 새로고침
                    }
                    else
                    {
                        MessageBox.Show("승인 처리 중 오류가 발생했습니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            else
            {
                MessageBox.Show("승인할 사용자를 선택해주세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void btnReject_Click(object sender, RoutedEventArgs e)
        {
            if (dgUsers.SelectedItem is User selectedUser)
            {
                if (selectedUser.IsAdmin)
                {
                    MessageBox.Show("관리자는 삭제할 수 없습니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var result = MessageBox.Show(
                    $"사용자 '{selectedUser.Username}'을(를) 거부/삭제하시겠습니까?\n\n이 작업은 되돌릴 수 없습니다.",
                    "사용자 삭제",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    bool success = await _dbService.DeleteUserAsync(selectedUser.Id);
                    
                    if (success)
                    {
                        MessageBox.Show($"사용자 '{selectedUser.Username}'이(가) 삭제되었습니다.", "삭제 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                        LoadUsers(); // 목록 새로고침
                    }
                    else
                    {
                        MessageBox.Show("삭제 처리 중 오류가 발생했습니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            else
            {
                MessageBox.Show("삭제할 사용자를 선택해주세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            LoadUsers();
        }

        private void OnMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void OnMaximize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void OnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }
    }
}
