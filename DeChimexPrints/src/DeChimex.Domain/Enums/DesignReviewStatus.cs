namespace DeChimex.Domain.Enums;

/// <summary>
/// Approval status for a design review submission. Unlike design proofs (which are
/// versioned and supersede previous ones), design reviews maintain independent history
/// so multiple submissions can coexist with their own approval state.
/// </summary>
public enum DesignReviewStatus
{
    /// <summary>Uploaded and awaiting review/approval.</summary>
    Pending = 0,
    
    /// <summary>Approved by authorized reviewer (staff by default).</summary>
    Approved = 1,
    
    /// <summary>Rejected with a reason provided.</summary>
    Rejected = 2
}
