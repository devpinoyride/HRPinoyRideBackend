namespace PinoyRideHrApi.Models;

public class Profile
{
    public Guid Id { get; set; }
    public string? Email { get; set; }
    public string? FullName { get; set; }
    public string? Department { get; set; }
    public string? Position { get; set; }
    public string? Role { get; set; }
    public string? Status { get; set; }
    public Guid? ApproverId { get; set; }
    public string? ApproverName { get; set; }
    public DateTime? CreatedAt { get; set; }
}

public class TimeEntry
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public DateOnly WorkDate { get; set; }
    public DateTime? TimeIn { get; set; }
    public DateTime? TimeOut { get; set; }
    public string? Source { get; set; }
    public string? Status { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public double? Hours { get; set; }
}

public class TimekeepingRequest
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public DateOnly WorkDate { get; set; }
    public TimeOnly? RequestedTimeIn { get; set; }
    public TimeOnly? RequestedTimeOut { get; set; }
    public string? RequestType { get; set; }
    public string? Reason { get; set; }
    public Guid? ApproverId { get; set; }
    public string? Status { get; set; }
    public string? ApproverNotes { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime? CreatedAt { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
}

public class AuditLog
{
    public long Id { get; set; }
    public Guid? ActorId { get; set; }
    public string? Action { get; set; }
    public string? TableName { get; set; }
    public string? RecordId { get; set; }
    public string? DetailsJson { get; set; }
    public DateTime? CreatedAt { get; set; }
}