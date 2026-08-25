namespace Technic_accounting_student.Models
{
    public class StudentGroup
    {
        public int GroupId { get; set; }
        public string GroupName { get; set; } = string.Empty;

        public override string ToString() => GroupName;
    }
}
