namespace Technic_accounting_student.Models
{
    public class SessionInfo
    {
        public int LogId { get; set; }

        public int UserId { get; set; }

        public int EquipmentId { get; set; }

        public DateTime StartDate { get; set; }

        public string AuthMethod { get; set; } = string.Empty;
    }
}
