using System.ComponentModel.DataAnnotations;
namespace DeChimex.Host.Models.Admin;
public sealed class MajorCategoryFormViewModel
{
    public Guid? Id { get; set; }
    [Required, StringLength(120)] public string Name { get; set; } = "";
    [StringLength(1000)] public string? Description { get; set; }
}
