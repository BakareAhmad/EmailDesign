# Design Review Attachment System - Implementation Plan

**Date:** 2026-09-18  
**Objective:** Implement document/file-based upload system for Design Review attachments

---

## 🎯 CURRENT STATE ANALYSIS

### Existing Components

#### 1. OrderDesignProof Entity
- **Purpose:** Staff-uploaded design proofs for customer approval
- **Storage:** Original file preserved (StoredFileName, OriginalFileName, Url, ContentType, SizeBytes)
- **Behavior:** Each new proof **supersedes** previous ones
- **Status:** PendingApproval, Approved, ChangesRequested, Superseded
- **Upload:** Staff only (via UploadDesignProofCommand)
- **Approval:** Customer approves/rejects via ApproveDesignProofCommand/RequestDesignChangesCommand
- **File Types:** Limited to images only (FileCategory.DesignProof → AllowedImageExtensions)

#### 2. File Storage System
- **Service:** `IFileStorageService` with `LocalFileStorageService` implementation
- **Categories:** ProductImage, GalleryMedia, InvoiceDocument, Document, OrderAttachment, DesignProof, ChatMedia
- **Validation:** Extension-based, size limits (10MB default)
- **Current Allowed Extensions:**
  - Images: .jpg, .jpeg, .png, .webp, .gif
  - Videos: .mp4, .webm
  - Audio: .webm, .mp3, .ogg, .oga, .wav, .m4a
  - Documents: .pdf, .doc, .docx

#### 3. Chat Media System
- **Purpose:** General conversation attachments (images, videos, audio, documents)
- **Entity:** OrderMessage.Attachments (OrderMessageAttachment)
- **File Category:** ChatMedia
- **Behavior:** Attached to messages, not versioned
- **File Types:** Images + Videos + Audio + Documents

#### 4. Real-time System
- **SignalR:** OrderChatHub with order-based groups
- **Push Events:** orderFile, orderProof, orderStatus, orderMessagesRead, orderMessagesDelivered
- **Mechanism:** IOrderChatPusher.PushOrderEventAsync()

---

## 🎯 REQUIREMENTS ANALYSIS

### What User Wants

1. **Document/File Treatment**
   - NOT chat media
   - NOT compressed/resized/re-encoded
   - Original file bytes preserved exactly
   - Original metadata preserved: filename, extension, MIME type, file size

2. **Supported File Types**
   - PNG, JPG/JPEG, WEBP (images)
   - PDF (document)
   - SVG (vector graphics)
   - PSD, AI, EPS (Adobe formats)
   - TIFF (high-res images)
   - ZIP (design packages)
   - Must follow existing security conventions

3. **Preview Behavior**
   - Previewable files (PNG/JPG/WEBP/PDF): Generate/lightweight preview
   - Non-previewable files (PSD/AI/EPS): Show document/file card
   - Preview NEVER replaces original
   - Download/open always retrieves original

4. **UI Behavior**
   - Visually communicate it's a file/document
   - For images: Show preview thumbnail + filename + extension + size + open action
   - For non-previewable: Show file icon + filename + extension + size + open/download
   - Don't expose technical storage details

5. **Workflow Requirements**
   - Design A submitted
   - Staff can reject Design A
   - Customer/staff can submit Design B
   - Design B can be rejected
   - Design C can be approved
   - All previous submissions remain as historical records
   - Approval/rejection applies to specific submission
   - NOT superseding behavior

6. **Security**
   - Don't trust client-provided extension/MIME type
   - Validate using existing conventions
   - Unauthorized users cannot upload/replace/access files
   - File paths not user-controlled
   - Dangerous files rejected
   - Filenames safely handled
   - Downloads authorized against order/design-review record
   - No physical server paths exposed

7. **Real-time**
   - Use existing SignalR architecture
   - Design-review upload/approval/rejection should update workspace in real-time

8. **Constraints**
   - Don't rewrite existing order workspace
   - Don't introduce OrderRequest aggregate/table
   - Preserve existing Order lifecycle
   - Reuse existing storage, auth, notifications, SignalR, UI infrastructure
   - Don't break existing chat media functionality
   - Don't break existing design proof functionality

---

## 🔍 ARCHITECTURE DECISION

### Option A: Extend OrderDesignProof

**Pros:**
- Reuses existing entity
- Less code duplication
- Familiar patterns

**Cons:**
- Current behavior supersedes previous proofs (conflicts with requirement)
- Only staff can upload (customers need to upload too)
- File category limited to images only
- Would require significant changes to existing behavior
- Could break existing tests/workflows

**Verdict:** ❌ Not suitable - Too many behavioral conflicts

---

### Option B: Create New OrderDesignReview Entity

**Pros:**
- Clean separation from existing design proofs
- Can implement independent history (no superseding)
- Can support both customer and staff uploads
- Can support document file types
- Preserves existing design proof system
- Follows existing patterns (similar to OrderDesignProof)

**Cons:**
- New entity/table
- Some code duplication (but minimal)

**Verdict:** ✅ **RECOMMENDED**

---

### Option C: Use OrderMessage Attachments with Special Type

**Pros:**
- Reuses existing attachment system
- No new entities

**Cons:**
- Chat media is conceptually different
- No approval/rejection workflow
- No versioning/history
- Would pollute chat attachments with design review files
- Confuses the two concepts

**Verdict:** ❌ Not suitable - Violates separation of concerns

---

## ✅ RECOMMENDED ARCHITECTURE

### New Entity: OrderDesignReview

```csharp
public sealed class OrderDesignReview : Entity
{
    // Identification
    public Guid OrderId { get; private set; }
    
    // File metadata (original preserved)
    public string StoredFileName { get; private set; }  // e.g., "abc123.psd"
    public string OriginalFileName { get; private set; } // e.g., "Jersey-Front-Final.psd"
    public string Url { get; private set; }                // Public URL
    public string ContentType { get; private set; }      // MIME type
    public long SizeBytes { get; private set; }           // Original file size
    
    // Preview (separate from original)
    public string? PreviewUrl { get; private set; }      // Preview image URL (if applicable)
    public string? PreviewStoredFileName { get; private set; }
    
    // Submission metadata
    public Guid UploadedByUserId { get; private set; }
    public string UploadedByRole { get; private set; }    // "customer" or "staff"
    public string? Notes { get; private set; }            // Optional notes from uploader
    
    // Approval workflow
    public DesignReviewStatus Status { get; private set; } // Pending, Approved, Rejected
    public string? RejectionReason { get; private set; }  // If rejected
    public Guid? ApprovedByUserId { get; private set; }   // Who approved
    public DateTime? ApprovedAtUtc { get; private set; }
    public Guid? RejectedByUserId { get; private set; }   // Who rejected
    public DateTime? RejectedAtUtc { get; private set; }
    
    // Timestamps
    public DateTime CreatedDateUtc { get; private set; }
}
```

### DesignReviewStatus Enum

```csharp
public enum DesignReviewStatus
{
    Pending = 0,    // Uploaded, awaiting review
    Approved = 1,   // Approved by staff/customer (configurable)
    Rejected = 2    // Rejected with reason
}
```

### Key Behaviors

1. **Multiple Submissions:** Each design review is independent, NOT superseding
2. **History Preserved:** All submissions remain in the collection
3. **Upload:** Both customer and staff can upload (with proper authorization)
4. **Approval:** Configurable who can approve (staff by default, or customer for their designs)
5. **File Types:** Extended to support design document formats

---

## 📁 FILE CHANGES REQUIRED

### 1. Domain Layer

#### New Files
- `src/DeChimex.Domain/Entities/OrderDesignReview.cs` - New entity
- `src/DeChimex.Domain/Enums/DesignReviewStatus.cs` - New enum
- `src/DeChimex.Domain/Events/OrderDesignReviewEvents.cs` - New events

#### Modified Files
- `src/DeChimex.Domain/Entities/Order.cs` - Add DesignReviews collection

### 2. Infrastructure Layer

#### Modified Files
- `src/DeChimex.Infrastructure/Options/AppOptions.cs` - Extend AllowedDocumentExtensions
- `src/DeChimex.Infrastructure/Storage/LocalFileStorageService.cs` - Handle design review files
- `src/DeChimex.Persistence/Configurations/CustomOrderConfigurations.cs` - Add DesignReview configuration
- `src/DeChimex.Persistence/Context/DeChimexDbContext.cs` - Add DbSet<OrderDesignReview>

### 3. Application Layer

#### New Files
- `src/DeChimex.Application/Features/Orders/Commands/DesignReviewCommands.cs` - New commands
- `src/DeChimex.Application/EventHandlers/OrderDesignReviewEventHandlers.cs` - New handlers

#### Modified Files
- `src/DeChimex.Application/Common/Interfaces/Services.cs` - Add FileCategory.DesignReview
- `src/DeChimex.Application/Features/Orders/Queries/CustomOrderQueries.cs` - Include DesignReviews
- `src/DeChimex.Contracts/Orders/OrderWorkflowDtos.cs` - Add OrderDesignReviewDto

### 4. Host Layer

#### New Files
- None (reuse existing controllers)

#### Modified Files
- `src/DeChimex.Host/Controllers/Mvc/Admin/AdminOrderWorkspaceController.cs` - Add upload endpoint
- `src/DeChimex.Host/Controllers/Mvc/Portal/PortalOrdersController.cs` - Add upload endpoint
- `src/DeChimex.Host/Views/AdminOrderWorkspace/Index.cshtml` - Add Design Review section
- `src/DeChimex.Host/Views/PortalOrders/Workspace.cshtml` - Add Design Review section

### 5. Real-time

#### Modified Files
- None - Use existing IOrderChatPusher

---

## 🎯 IMPLEMENTATION PHASES

### Phase 1: Domain Layer (Foundation)

1. Create `DesignReviewStatus` enum
2. Create `OrderDesignReview` entity
3. Create domain events:
   - `DesignReviewUploadedEvent`
   - `DesignReviewApprovedEvent`
   - `DesignReviewRejectedEvent`
4. Add `DesignReviews` collection to Order entity
5. Add domain methods to Order:
   - `AddDesignReview(...)`
   - `ApproveDesignReview(Guid designReviewId, Guid userId, string? notes)`
   - `RejectDesignReview(Guid designReviewId, Guid userId, string reason)`

### Phase 2: Infrastructure Layer (Storage)

1. Extend `FileCategory` enum with `DesignReview`
2. Extend `AllowedDocumentExtensions` in StorageOptions to include:
   - .png, .jpg, .jpeg, .webp, .gif (images)
   - .pdf (documents)
   - .svg (vector)
   - .psd, .ai, .eps (Adobe)
   - .tiff, .tif (high-res)
   - .zip (packages)
3. Update `LocalFileStorageService` to:
   - NOT compress/resize DesignReview files
   - Generate previews for previewable types
   - Store preview separately
4. Add DbSet<OrderDesignReview> to DbContext
5. Add EF configuration for OrderDesignReview

### Phase 3: Application Layer (Commands/Queries)

1. Add `FileCategory.DesignReview` to Services.cs
2. Create commands:
   - `UploadDesignReviewCommand` (customer/staff)
   - `ApproveDesignReviewCommand` (staff)
   - `RejectDesignReviewCommand` (staff)
3. Create DTOs:
   - `OrderDesignReviewDto`
   - `UploadDesignReviewRequest`
   - `ApproveDesignReviewRequest`
   - `RejectDesignReviewRequest`
4. Update workspace queries to include DesignReviews
5. Create event handlers for design review events

### Phase 4: Host Layer (UI)

1. Add upload endpoints to both admin and portal controllers
2. Add Design Review section to workspace views:
   - List all design reviews with status
   - Show preview for previewable files
   - Show document card for non-previewable files
   - Show uploader, timestamp, status
   - Provide approve/reject buttons (for staff)
   - Provide download/open links
3. Add real-time push for design review events

---

## 🔐 SECURITY CONSIDERATIONS

### 1. File Validation
- Use existing `LocalFileStorageService` validation
- Extend allowed extensions for DesignReview category
- Validate file size (consider increasing limit for design files)
- Validate MIME type against extension
- Don't trust client-provided data

### 2. Authorization
- Customer can only upload to their own orders
- Customer can only see their own order's design reviews
- Staff can upload to any assigned order
- Staff can approve/reject any order's design reviews
- Downloads must check authorization

### 3. Storage Security
- Use GUID-based stored file names
- Never expose physical paths
- Store in dedicated "designreview" folder
- Prevent path traversal attacks

### 4. Dangerous Files
- Reject executable files (.exe, .dll, .bat, etc.)
- Reject script files (.js, .php, .py, etc.)
- Consider virus scanning for uploaded files
- ZIP files: Consider scanning contents or limiting to known-safe archives

---

## 📊 FILE TYPE HANDLING

### Previewable Types (Generate Preview)
| Extension | MIME Type | Preview Strategy |
|-----------|-----------|-----------------|
| .png | image/png | Direct display (browser native) |
| .jpg, .jpeg | image/jpeg | Direct display |
| .webp | image/webp | Direct display |
| .gif | image/gif | Direct display |
| .pdf | application/pdf | Generate PNG thumbnail (first page) |
| .svg | image/svg+xml | Direct display or generate PNG |
| .tiff, .tif | image/tiff | Generate JPEG/PNG preview |

### Non-Previewable Types (Document Card)
| Extension | MIME Type | Display |
|-----------|-----------|---------|
| .psd | application/psd, image/vnd.adobe.photoshop | File icon + metadata |
| .ai | application/illustrator, application/pdf | File icon + metadata |
| .eps | application/postscript, application/eps | File icon + metadata |
| .zip | application/zip | File icon + metadata |

### Preview Generation Strategy

```csharp
// In LocalFileStorageService.UploadAsync() for DesignReview category
if (IsPreviewable(extension))
{
    var previewStoredName = $"preview_{storedName}";
    var previewPath = Path.Combine(root, previewStoredName);
    
    if (extension is ".pdf")
    {
        // Generate PDF thumbnail (first page as PNG)
        await GeneratePdfPreview(request.Content, previewPath);
    }
    else if (IsImage(extension))
    {
        // For large images, generate a thumbnail
        await GenerateImageThumbnail(request.Content, previewPath, maxWidth: 400);
    }
    
    return new FileUploadResult(url, storedName, finalSize, previewUrl: $"{publicBaseUrl}/designreview/{previewStoredName}");
}
```

**Note:** Preview generation should be best-effort. If it fails, the original file is still stored and accessible.

---

## 🎨 UI COMPONENTS

### Design Review Card (Previewable)

```html
<div class="design-review-card" data-design-review-id="@dr.Id">
    <div class="design-review-header">
        <span class="design-review-version">Design Review #@dr.Version</span>
        <span class="badge @GetStatusBadgeClass(dr.Status)">@dr.Status</span>
    </div>
    
    <div class="design-review-preview">
        <img src="@dr.PreviewUrl" alt="Preview" />
    </div>
    
    <div class="design-review-meta">
        <div class="design-review-filename">@dr.OriginalFileName</div>
        <div class="design-review-details">
            <span>@GetFileType(dr.OriginalFileName) · @FormatFileSize(dr.SizeBytes)</span>
        </div>
    </div>
    
    <div class="design-review-actions">
        <a href="@dr.Url" download class="btn btn-outline btn-sm">Download Original</a>
        @if (User.IsStaff() && dr.Status == DesignReviewStatus.Pending)
        {
            <button class="btn btn-success btn-sm" onclick="approveDesignReview('@dr.Id')">Approve</button>
            <button class="btn btn-outline btn-sm" onclick="showRejectModal('@dr.Id')">Reject</button>
        }
    </div>
    
    @if (dr.Status == DesignReviewStatus.Rejected)
    {
        <div class="design-review-rejection">
            <strong>Rejected:</strong> @dr.RejectionReason
        </div>
    }
    
    <div class="design-review-timestamp">
        Uploaded @dr.CreatedDateUtc.ToLocalTime().ToString("d MMM, HH:mm") 
        by @dr.UploadedByRole
    </div>
</div>
```

### Design Review Card (Non-Previewable)

```html
<div class="design-review-card document-card" data-design-review-id="@dr.Id">
    <div class="design-review-header">
        <span class="design-review-version">Design Review #@dr.Version</span>
        <span class="badge @GetStatusBadgeClass(dr.Status)">@dr.Status</span>
    </div>
    
    <div class="design-review-icon">
        <svg>...</svg> <!-- File icon based on extension -->
    </div>
    
    <div class="design-review-meta">
        <div class="design-review-filename">@dr.OriginalFileName</div>
        <div class="design-review-details">
            <span>@GetFileType(dr.OriginalFileName) · @FormatFileSize(dr.SizeBytes)</span>
        </div>
    </div>
    
    <div class="design-review-actions">
        <a href="@dr.Url" download class="btn btn-outline btn-sm">Download</a>
        @if (User.IsStaff() && dr.Status == DesignReviewStatus.Pending)
        {
            <button class="btn btn-success btn-sm" onclick="approveDesignReview('@dr.Id')">Approve</button>
            <button class="btn btn-outline btn-sm" onclick="showRejectModal('@dr.Id')">Reject</button>
        }
    </div>
    
    @if (dr.Status == DesignReviewStatus.Rejected)
    {
        <div class="design-review-rejection">
            <strong>Rejected:</strong> @dr.RejectionReason
        </div>
    }
    
    <div class="design-review-timestamp">
        Uploaded @dr.CreatedDateUtc.ToLocalTime().ToString("d MMM, HH:mm") 
        by @dr.UploadedByRole
    </div>
</div>
```

---

## 📋 WORKFLOW

### Upload Flow

```
User (Customer or Staff) → Upload Design Review File
    ↓
Validate file (size, extension, MIME type)
    ↓
Store original file (with GUID filename)
    ↓
If previewable: Generate preview
    ↓
Create OrderDesignReview entity
    ↓
Add to Order.DesignReviews
    ↓
Save to database
    ↓
Add timeline entry
    ↓
Push real-time event (orderDesignReview)
    ↓
Return success with DTO
```

### Approval Flow

```
Staff → Approve Design Review
    ↓
Verify authorization (staff, order access)
    ↓
Get OrderDesignReview by ID
    ↓
Verify status is Pending
    ↓
Update status to Approved
    ↓
Set ApprovedByUserId and ApprovedAtUtc
    ↓
Add timeline entry
    ↓
Push real-time event (orderDesignReviewStatus)
    ↓
Send notification to customer
    ↓
Return success
```

### Rejection Flow

```
Staff → Reject Design Review
    ↓
Verify authorization (staff, order access)
    ↓
Get OrderDesignReview by ID
    ↓
Verify status is Pending
    ↓
Update status to Rejected
    ↓
Set RejectedByUserId, RejectedAtUtc, RejectionReason
    ↓
Add timeline entry
    ↓
Push real-time event (orderDesignReviewStatus)
    ↓
Send notification to customer
    ↓
Return success
```

---

## 🧪 TESTING STRATEGY

### Unit Tests
1. OrderDesignReview entity creation and validation
2. Order.AddDesignReview() method
3. Order.ApproveDesignReview() method
4. Order.RejectDesignReview() method
5. File validation for all supported types
6. Preview generation for previewable types

### Integration Tests
1. Upload design review (customer)
2. Upload design review (staff)
3. Approve design review
4. Reject design review
5. Multiple design reviews maintain history
6. Authorization checks
7. Real-time push events

### Manual Tests
1. Upload PNG → Verify original preserved, preview generated
2. Upload PDF → Verify original preserved, preview generated
3. Upload PSD → Verify original preserved, document card displayed
4. Upload ZIP → Verify original preserved, document card displayed
5. Upload multiple designs → Verify all remain in history
6. Approve design → Verify status updates, notifications sent
7. Reject design → Verify status updates, reason recorded
8. Download original → Verify file is unchanged

---

## 🎯 ACCEPTANCE CRITERIA CHECKLIST

- [ ] Uploading `design-final.png` through Design Review preserves the original PNG
- [ ] Uploading a high-resolution design does not silently resize/compress the stored original
- [ ] The workspace can show a lightweight preview without replacing the original
- [ ] The UI displays the original filename and extension
- [ ] The original file can be downloaded/opened
- [ ] PDF/design files are handled as documents rather than forced into the normal media pipeline
- [ ] Unsupported/dangerous files are rejected appropriately
- [ ] Multiple design submissions maintain independent history
- [ ] Rejecting one design does not modify another design submission
- [ ] Approving a design identifies the exact design-review submission that was approved
- [ ] Existing general chat media uploads continue to work unchanged
- [ ] Existing order/chat/workspace functionality remains intact
- [ ] Existing permissions and authorization are preserved
- [ ] Existing tests are updated/added as necessary

---

## 📝 NEXT STEPS

**Before Implementation:**
1. Review this plan and confirm architecture
2. Decide on preview generation approach (library, external service, or skip for MVP)
3. Confirm file type support list
4. Confirm size limits for design files

**Implementation Order:**
1. Phase 1: Domain Layer (Entity, Events, Order methods)
2. Phase 2: Infrastructure Layer (Storage, DbContext, EF Config)
3. Phase 3: Application Layer (Commands, DTOs, Queries)
4. Phase 4: Host Layer (Controllers, Views)
5. Testing and Verification

**Estimated Complexity:** Medium (5-8 files, ~300-500 lines of code)

---

*Document complete. Ready for implementation upon approval.*
