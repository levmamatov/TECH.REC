using Technic_accounting_student.Models;

namespace Technic_accounting_student.Services
{
    public static class SessionManager
    {
        public static SessionInfo? CurrentSession { get; set; }

        public static bool IsAuthorized
        {
            get
            {
                return CurrentSession != null;
            }
        }

        public static void Clear()
        {
            CurrentSession = null;
        }
    }
}
