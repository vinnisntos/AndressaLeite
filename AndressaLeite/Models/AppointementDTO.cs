namespace AndressaLeite.Models
{
    // DTO (Data Transfer Object) não precisa herdar de BaseModel 
    // porque serve apenas para moldar os dados que vão para a tela.
    public class AppointmentDTO
    {
        public string Id { get; set; } = string.Empty;
        public string ServiceName { get; set; } = string.Empty;
        public string EmployeeName { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public string? BookedForName { get; set; }
    }
}