using System.Data;
using System.Data.SqlClient;
using Technic_accounting_student.Models;

namespace Technic_accounting_student.Data
{
    public sealed class DbManager
    {
        private readonly string _connectionString;

        public DbManager(string? connectionString = null)
        {
            _connectionString = connectionString
                ?? @"Server=26.197.58.130,1433; Database=CourseworkPractice; User ID=sa; Password=12345; TrustServerCertificate=True;";
        }

        public List<StudentGroup> GetGroups()
        {
            var result = new List<StudentGroup>();

            const string sql = @"
                SELECT group_id, group_name
                FROM Groups
                ORDER BY group_name;";

            using (var conn = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(sql, conn))
            {
                conn.Open();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new StudentGroup
                        {
                            GroupId = Convert.ToInt32(reader["group_id"]),
                            GroupName = reader["group_name"].ToString() ?? string.Empty
                        });
                    }
                }
            }

            return result;
        }

        public EquipmentItem? GetEquipmentByMachineName(string machineName)
        {
            if (string.IsNullOrWhiteSpace(machineName))
                return null;

            const string sql = @"
                SELECT TOP 1 equipment_id, inventory_number, machine_name
                FROM Equipment
                WHERE machine_name = @machine_name;";

            using (var conn = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.Add("@machine_name", SqlDbType.NVarChar, 100).Value = machineName.Trim();

                conn.Open();
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return new EquipmentItem
                    {
                        EquipmentId = Convert.ToInt32(reader["equipment_id"]),
                        InventoryNumber = reader["inventory_number"].ToString() ?? string.Empty,
                        MachineName = reader["machine_name"].ToString() ?? string.Empty
                    };
                }
            }
        }

        public UserItem? FindUser(string lastName, string firstName, int groupId, string studentCardNumber)
        {
            if (string.IsNullOrWhiteSpace(lastName) ||
                string.IsNullOrWhiteSpace(firstName) ||
                string.IsNullOrWhiteSpace(studentCardNumber))
            {
                return null;
            }

            const string sql = @"
                SELECT TOP 1
                    user_id,
                    last_name,
                    first_name,
                    middle_name,
                    course,
                    group_id,
                    student_card_number,
                    telegram_id,
                    is_verified
                FROM Users
                WHERE last_name = @last_name
                  AND first_name = @first_name
                  AND group_id = @group_id
                  AND student_card_number = @student_card_number;";

            using (var conn = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.Add("@last_name", SqlDbType.NVarChar, 100).Value = lastName.Trim();
                cmd.Parameters.Add("@first_name", SqlDbType.NVarChar, 100).Value = firstName.Trim();
                cmd.Parameters.Add("@group_id", SqlDbType.Int).Value = groupId;
                cmd.Parameters.Add("@student_card_number", SqlDbType.NVarChar, 50).Value = studentCardNumber.Trim();

                conn.Open();
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return new UserItem
                    {
                        UserId = Convert.ToInt32(reader["user_id"]),
                        LastName = reader["last_name"].ToString() ?? string.Empty,
                        FirstName = reader["first_name"].ToString() ?? string.Empty,
                        MiddleName = reader["middle_name"] == DBNull.Value ? null : reader["middle_name"].ToString(),
                        Course = Convert.ToInt32(reader["course"]),
                        GroupId = Convert.ToInt32(reader["group_id"]),
                        StudentCardNumber = reader["student_card_number"].ToString() ?? string.Empty,
                        TelegramId = reader["telegram_id"] == DBNull.Value ? null : Convert.ToInt64(reader["telegram_id"]),
                        IsVerified = Convert.ToBoolean(reader["is_verified"])
                    };
                }
            }
        }

        public SessionInfo CreateSession(int userId, int equipmentId, string authMethod)
        {
            if (userId <= 0) throw new ArgumentException("Некорректный userId.", nameof(userId));
            if (equipmentId <= 0) throw new ArgumentException("Некорректный equipmentId.", nameof(equipmentId));
            if (string.IsNullOrWhiteSpace(authMethod)) throw new ArgumentException("Не указан способ подтверждения.", nameof(authMethod));

            const string sql = @"
                INSERT INTO UsageLog (user_id, equipment_id, start_date, end_date, auth_method)
                OUTPUT INSERTED.log_id, INSERTED.start_date
                VALUES (@user_id, @equipment_id, GETDATE(), NULL, @auth_method);";

            using (var conn = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
                cmd.Parameters.Add("@equipment_id", SqlDbType.Int).Value = equipmentId;
                cmd.Parameters.Add("@auth_method", SqlDbType.NVarChar, 20).Value = authMethod.Trim();

                conn.Open();
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                        throw new Exception("Не удалось создать запись сессии.");

                    return new SessionInfo
                    {
                        LogId = Convert.ToInt32(reader["log_id"]),
                        UserId = userId,
                        EquipmentId = equipmentId,
                        StartDate = Convert.ToDateTime(reader["start_date"]),
                        AuthMethod = authMethod.Trim()
                    };
                }
            }
        }

        public bool CloseSession(int logId)
        {
            if (logId <= 0)
                return false;

            const string sql = @"
                UPDATE UsageLog
                SET end_date = GETDATE()
                WHERE log_id = @log_id
                  AND end_date IS NULL;";

            using (var conn = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.Add("@log_id", SqlDbType.Int).Value = logId;

                conn.Open();
                int rows = cmd.ExecuteNonQuery();
                return rows > 0;
            }
        }

        public SessionInfo? GetOpenSessionByEquipmentId(int equipmentId)
        {
            if (equipmentId <= 0)
                return null;

            const string sql = @"
                SELECT TOP 1 log_id, user_id, equipment_id, start_date, auth_method
                FROM UsageLog
                WHERE equipment_id = @equipment_id
                  AND end_date IS NULL
                ORDER BY start_date DESC;";

            using (var conn = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.Add("@equipment_id", SqlDbType.Int).Value = equipmentId;

                conn.Open();
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return new SessionInfo
                    {
                        LogId = Convert.ToInt32(reader["log_id"]),
                        UserId = Convert.ToInt32(reader["user_id"]),
                        EquipmentId = Convert.ToInt32(reader["equipment_id"]),
                        StartDate = Convert.ToDateTime(reader["start_date"]),
                        AuthMethod = reader["auth_method"] == DBNull.Value
                            ? string.Empty
                            : reader["auth_method"].ToString() ?? string.Empty
                    };
                }
            }
        }
    }
}