namespace Technic_accounting_student.Models
{
    public class UserItem
    {
        public int UserId { get; set; }
        public string LastName { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string? MiddleName { get; set; }
        public int Course { get; set; }
        public int GroupId { get; set; }
        public string StudentCardNumber { get; set; } = string.Empty;
        public long? TelegramId { get; set; }
        public bool IsVerified { get; set; }
    }
}
