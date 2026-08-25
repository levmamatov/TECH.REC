using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Technic_accounting_student.Models
{
    public class SupportTicketDto
    {
        public int? UserId { get; set; }
        public int? EquipmentId { get; set; }
        public string Title { get; set; } // "Регистрация" или "Помощь"
        public string Description { get; set; }
        public string StudentNameRaw { get; set; }
        public string TelegramRaw { get; set; }
        public string Status { get; set; } = "Новое";
    }
}
