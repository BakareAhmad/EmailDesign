using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Common;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Events;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DeChimex.Persistence.Interceptors;

/// <summary>Allowlisted operational projection at the existing atomic save boundary.
/// No controller, notification or SignalR handler writes a second copy of these facts.</summary>
internal static class BusinessActivityCapture
{
    public static List<AuditLog> Capture(DeChimexDbContext db, ICurrentUser user, DateTime now)
    {
        db.ChangeTracker.DetectChanges();
        var entries = db.ChangeTracker.Entries().ToList();
        var batch = Guid.NewGuid().ToString("N");
        var rows = new List<AuditLog>();
        var role = user.UserId is null ? "System" : user.Roles.Contains("Customer") ? "Customer" : "Staff";
        // One whitelisted identity lookup per save, never email/name/contact details.
        var username = role == "Staff" ? db.Users.AsNoTracking().Where(u => u.Id == user.UserId)
            .Select(u => u.Username).FirstOrDefault()?.Value : null;
        var actor = username ?? role;
        bool Changed(EntityEntry e, string p) => e.Metadata.FindProperty(p) is not null &&
            !Equals(e.Property(p).OriginalValue, e.Property(p).CurrentValue);
        bool Any(EntityEntry e, params string[] props) => props.Any(p => Changed(e,p));
        bool Added(EntityEntry e) => e.State == EntityState.Added;
        bool Deleted(EntityEntry e) => e.State == EntityState.Deleted || (e.Entity is AggregateRoot a && a.IsDeleted && Changed(e,"IsDeleted"));
        string Verb(EntityEntry e) => Deleted(e) ? "deleted" : Added(e) ? "created" : "updated";
        string Key(EntityEntry e, string type) => Added(e) ? $"{type}:{((Entity)e.Entity).Id}" : $"{batch}:{((Entity)e.Entity).Id}:{type}";
        void Add(EntityEntry e, string type, string category, string title, string text, bool live=false,
            Guid? order=null, Guid? customer=null, Guid? product=null, Guid? staff=null,
            string importance="normal", string? key=null, Guid? actorId=null, string? actorName=null)
        {
            var id=((Entity)e.Entity).Id;
            var a=AuditLog.Create(Added(e)?"create":Deleted(e)?"delete":"update", e.Metadata.ClrType.Name, id.ToString(), user.Email ?? user.UserId?.ToString() ?? "system");
            a.Describe(type,category,title,text,user.UserId,actor,role,key ?? Key(e,type),live,order,customer,product,staff,importance,now);
            if(actorName is not null) a.Describe(type,category,title,text,actorId,actorName,actorId is null?"System":"Staff",key??Key(e,type),live,order,customer,product,staff,importance,now);
            // A direct DbContext caller may not clear domain events. Never replay already staged/persisted sources.
            if(!db.AuditLogs.Local.Any(x=>x.SourceKey==a.SourceKey) && !rows.Any(x=>x.SourceKey==a.SourceKey)) rows.Add(a);
        }
        (object? Old,object? New) Owned(EntityEntry owner,string navigation,string property)
        {
            var current=owner.Reference(navigation).TargetEntry;
            if(current is null)return (null,null);
            var ownership=current.Metadata.FindOwnership()!;
            var fk=ownership.Properties[0].Name;
            var ownerId=((Entity)owner.Entity).Id;
            var removed=entries.FirstOrDefault(x=>x.Metadata==current.Metadata && x.State==EntityState.Deleted && Equals(x.Property(fk).CurrentValue,ownerId));
            return (removed?.Property(property).OriginalValue ?? current.Property(property).OriginalValue,current.Property(property).CurrentValue);
        }
        bool OwnedChanged(EntityEntry e,string nav,string property) { var v=Owned(e,nav,property);return !Equals(v.Old,v.New); }
        foreach(var e in entries.Where(x=>x.Entity is AggregateRoot))
        {
            switch(e.Entity)
            {
                case Product p when Added(e) || Deleted(e) || Any(e,"Name","Description","CategoryId","IsActive","IsFeatured","IsCustomizable") || OwnedChanged(e,"SellingPrice","Amount"):
                    var price=Owned(e,"SellingPrice","Amount");
                    var priceChanged=!Added(e) && !Equals(price.Old,price.New);
                    var categoryChanged=!Added(e) && Changed(e,"CategoryId");
                    var type=Added(e)||Deleted(e)?$"product.{Verb(e)}":priceChanged?"product.price_changed":categoryChanged?"product.category_changed":Changed(e,"IsActive")?(p.IsActive?"product.activated":"product.archived"):"product.updated";
                    var detail=$"Product {p.Name}.";
                    if(priceChanged) detail+=$" Price changed from {price.Old} to {price.New} {p.SellingPrice.Currency}.";
                    if(categoryChanged) detail+=" Subcategory changed.";
                    Add(e,type,"products",type=="product.price_changed"?"Product price changed":type=="product.category_changed"?"Product category changed":$"Product {Verb(e)}",detail,product:p.Id);break;
                case Category c when Added(e)||Deleted(e)||Any(e,"Name","Description","MajorCategoryId","IsActive","DisplayOrder"):
                    Add(e,Changed(e,"MajorCategoryId")&&!Added(e)?"category.moved":$"category.{Verb(e)}","categories",Changed(e,"MajorCategoryId")&&!Added(e)?"Subcategory reassigned":$"Subcategory {Verb(e)}",$"Subcategory {c.Name}.");break;
                case MajorCategory m when Added(e)||Deleted(e)||Any(e,"Name","Description"):
                    Add(e,$"major_category.{Verb(e)}","categories",$"Major category {Verb(e)}",$"Major category {m.Name}.");break;
                case Payment p when Added(e)||Changed(e,"Status")||Changed(e,"RefundStatus"):
                    if(p.Status==PaymentStatus.Succeeded)
                    {
                        var po=db.Orders.AsNoTracking().FirstOrDefault(x=>x.Id==p.OrderId);
                        var direct=po?.Kind==OrderKind.Direct;
                        Add(e,"payment.succeeded","payments",direct?"Direct order payment received":"Payment received",direct?$"Direct order payment of {p.Amount.Amount} {p.Amount.Currency} confirmed.":$"Payment of {p.Amount.Amount} {p.Amount.Currency} confirmed for the linked order.",true,p.OrderId,p.CustomerId,key:$"payment:{p.Id}:succeeded");
                    }
                    else if(p.Status==PaymentStatus.Refunded) Add(e,"payment.refund_recorded","payments",p.Method==PaymentMethod.Paystack?"Gateway refund request accepted":"Manual refund recorded",p.Method==PaymentMethod.Paystack?"The provider accepted the refund request. Final settlement is not confirmed by this record.":"Staff recorded a refund of a manual payment; this is not a gateway transfer confirmation.",true,p.OrderId,p.CustomerId,key:$"payment:{p.Id}:refund");
                    // Failed attempt: business-visible once, keyed per payment. The summary is
                    // deliberately generic — FailureReason can carry provider payloads.
                    else if(p.Status==PaymentStatus.Failed) Add(e,"payment.failed","payments","Payment attempt failed",$"The payment attempt of {p.Amount.Amount} {p.Amount.Currency} failed; the customer can retry checkout or pay another way. The order stays open until a payment succeeds.",true,p.OrderId,p.CustomerId,key:$"payment:{p.Id}:failed");
                    else if(Added(e)) Add(e,"payment.initiated","payments","Payment initiated",$"Payment attempt of {p.Amount.Amount} {p.Amount.Currency} created.",false,p.OrderId,p.CustomerId);
                    // AUD-005: refund settlement lifecycle reported by the provider's refund.* events.
                    else if(Changed(e,"RefundStatus")&&p.RefundStatus==RefundStatus.Failed) Add(e,"payment.refund_failed","payments","Refund FAILED at provider",$"The provider reported that the refund of {p.Amount.Amount} {p.Amount.Currency} did NOT settle. The order currently reads as refunded but the customer has not received the money. Reconcile manually with the provider.",true,p.OrderId,p.CustomerId,importance:"attention",key:$"payment:{p.Id}:refund_failed");
                    else if(Changed(e,"RefundStatus")&&p.RefundStatus==RefundStatus.Processed) Add(e,"payment.refund_settled","payments","Refund settled",$"The provider confirmed the refund of {p.Amount.Amount} {p.Amount.Currency} reached the customer.",false,p.OrderId,p.CustomerId,key:$"payment:{p.Id}:refund_settled");
                    break;
                case ApplicationSetting s when Changed(e,"Value") && s.Key=="service_fee.percent":
                    Add(e,"settings.service_fee_changed","payments","Service fee configuration changed",
                        $"Service fee changed from {e.Property("Value").OriginalValue}% to {s.Value}%. Applies to future transactions; existing orders and completed transactions are not recalculated.",
                        true,key:$"setting:service_fee:{((Entity)e.Entity).Id}:{batch}");
                    break;
                case Order o:
                    if(Added(e)) Add(e,"order.created","orders",o.Kind==OrderKind.Direct?"Direct product order created":"Order created",o.Kind==OrderKind.Direct?$"Direct product order {o.OrderNumber} created.":$"Order {o.OrderNumber} created.",true,o.Id,o.CustomerId);
                    else if(Changed(e,"Status")) Add(e,"order.status_changed","orders",o.Kind==OrderKind.Direct?"Direct order status changed":"Order status changed",o.Kind==OrderKind.Direct?$"Direct order {o.OrderNumber}: {e.Property("Status").OriginalValue} → {o.Status}.":$"Order {o.OrderNumber}: {e.Property("Status").OriginalValue} → {o.Status}.",true,o.Id,o.CustomerId);
                    foreach(var final in o.DomainEvents.OfType<OrderFinalizedEvent>())
                        Add(e,"order.pricing_changed","orders","Order pricing finalized",$"Order {o.OrderNumber}: finalized total {final.GrandTotal} {o.GrandTotal.Currency}.",true,o.Id,o.CustomerId,key:$"event:{final.EventId}");
                    if(!Added(e)&&Changed(e,"ConversationState")&&o.ConversationState==OrderConversationState.Resolved) Add(e,"conversation.resolved","communication","Conversation resolved",$"Conversation for order {o.OrderNumber} resolved.",true,o.Id,o.CustomerId);
                    foreach(var evt in o.DomainEvents.OfType<SalesAssignedEvent>())
                        Add(e,evt.IsReassignment?"order.reassigned":"order.assigned","orders",evt.IsReassignment?"Order reassigned":"Order assigned",$"Order {o.OrderNumber} assigned to {evt.NewSalesName}.",true,o.Id,o.CustomerId,staff:evt.NewSalesUserId,key:$"event:{evt.EventId}",actorId:evt.ActorUserId,actorName:evt.ActorUserId is null?"System":actor);
                    break;
                case Customer c when Added(e)||Any(e,"FirstName","LastName","Notes","UserId","MarketingSubscribed") || OwnedChanged(e,"Phone","Value") || OwnedChanged(e,"Email","Value"):
                    Add(e,Added(e)?"customer.created":"customer.updated","customers",Added(e)?"Customer account created":"Customer profile updated","A customer record was "+(Added(e)?"created.":"updated. Contact values are not copied into the audit feed."),customer:c.Id);break;
                case User u when u.Username is not null && (Added(e)||Deleted(e)||Any(e,"IsActive")||OwnedChanged(e,"Username","Value")):
                    if(Deleted(e)){Add(e,"staff.deleted","staff","Staff account removed",$"Staff account {u.StaffDisplayName}.",true,staff:u.Id,importance:"attention");break;}
                    var activeChanged=!Added(e)&&Changed(e,"IsActive");
                    Add(e,Added(e)?"staff.created":activeChanged?(u.IsActive?"staff.enabled":"staff.disabled"):"staff.username_changed","staff",Added(e)?"Staff account created":activeChanged?(u.IsActive?"Staff account enabled":"Staff account disabled"):"Staff username changed",$"Staff account {u.StaffDisplayName}.",activeChanged,staff:u.Id,importance:activeChanged?"attention":"normal");break;
                case Role r when Added(e)||Deleted(e)||Any(e,"Name","Description"):
                    Add(e,$"role.{Verb(e)}","staff",$"Role {Verb(e)}",$"Role {r.Name}.",Deleted(e),importance:Deleted(e)?"attention":"normal");break;
                case Review r when Added(e)||Deleted(e)||Changed(e,"IsApproved"):
                    Add(e,Added(e)?"review.submitted":Deleted(e)?"review.deleted":"review.moderated","reviews",Added(e)?"Product review submitted":Deleted(e)?"Product review deleted":"Review publication changed",Added(e)?$"A {r.Rating}-star review was submitted; moderation is required.":Deleted(e)?"A review was removed.":r.IsApproved?"A review was approved for publication.":"A review is no longer approved for publication.",customer:r.CustomerId,product:r.ProductId);break;
            }
        }
        foreach(var e in entries)
        {
            Guid? Customer(Guid orderId) => entries.Select(x=>x.Entity).OfType<Order>().FirstOrDefault(o=>o.Id==orderId)?.CustomerId;
            switch(e.Entity)
            {
                case OrderDesignProof p when Added(e)||Changed(e,"Status"):
                    if(p.Status==DesignProofStatus.Superseded)break;
                    Add(e,Added(e)?"design.proof_uploaded":$"design.proof_{p.Status.ToString().ToLowerInvariant()}","design",Added(e)?"Design version submitted":$"Design proof {p.Status}",$"Design proof version {p.Version}: {p.Status}.",true,p.OrderId,Customer(p.OrderId));break;
                case OrderDesignReview r when Added(e)||Changed(e,"Status"):
                    Add(e,Added(e)?"design.review_uploaded":$"design.review_{r.Status.ToString().ToLowerInvariant()}","design",Added(e)?"Design review uploaded":$"Design review {r.Status}",$"Design review status: {r.Status}. Feedback and file contents are not copied.",true,r.OrderId,Customer(r.OrderId));break;
                case OrderTimelineEntry t when Added(e) && t.Type is "discussion" or "confirmed" or "file_uploaded" or "file_removed":
                    Add(e,$"workspace.{t.Type}","communication",t.Type=="discussion"?"Order conversation started":t.Type=="confirmed"?"Requirements confirmed":t.Type=="file_uploaded"?"Order attachment uploaded":"Order attachment removed","An order workspace milestone was recorded.",t.Type is "discussion" or "confirmed",t.OrderId,Customer(t.OrderId),key:$"timeline:{t.Id}");break;
                case InventoryTransaction t when Added(e)&&t.Reference!="initial":
                    Add(e,"inventory.movement","inventory","Stock movement recorded",$"{t.Type}: change {t.QuantityChange}; resulting quantity {t.ResultingQuantity}.",product:t.ProductId,key:$"inventory:{t.Id}");break;
            }
        }
        // Category.ReplaceOptions rebuilds child IDs. Compare logical option graphs,
        // not IDs, so saving an unchanged checklist is not business activity.
        foreach(var root in entries.Where(e=>e.Entity is Category && !Added(e)&&!Deleted(e)))
        {
            var cat=(Category)root.Entity;
            var options=entries.Where(e=>e.Entity is CategoryOption o&&o.CategoryId==cat.Id).ToList();
            if(!options.Any(e=>e.State is EntityState.Added or EntityState.Deleted))continue;
            string Signature(bool old)
            {
                var set=options.Where(e=>old?e.State!=EntityState.Added:e.State!=EntityState.Deleted).Select(e=>(CategoryOption)e.Entity).ToList();
                return System.Text.Json.JsonSerializer.Serialize(set.OrderBy(o=>o.DisplayOrder).Select(o=>new {
                    o.Question,o.Type,o.IsRequired,o.DisplayOrder,o.ShowWhenValue,
                    Parent=set.FirstOrDefault(p=>p.Id==o.ParentOptionId)?.Question,
                    Choices=entries.Where(e=>e.Entity is CategoryOptionChoice c&&c.CategoryOptionId==o.Id&&
                        (old?e.State!=EntityState.Added:e.State!=EntityState.Deleted))
                        .Select(e=>(CategoryOptionChoice)e.Entity).OrderBy(c=>c.DisplayOrder).Select(c=>c.Label).ToArray()
                }).ToArray());
            }
            if(Signature(true)!=Signature(false))
                Add(root,"category.options_changed","categories","Ordering options changed",$"Ordering requirements for subcategory {cat.Name} changed.");
        }
        foreach(var group in entries.Where(e=>e.Entity is UserRole && e.State is EntityState.Added or EntityState.Deleted).GroupBy(e=>((UserRole)e.Entity).UserId))
        {
            if(entries.Any(e=>e.Entity is User u&&u.Id==group.Key&&Added(e)))continue;
            var add=group.Where(Added).Select(e=>((UserRole)e.Entity).RoleId).ToHashSet();
            var remove=group.Where(e=>e.State==EntityState.Deleted).Select(e=>((UserRole)e.Entity).RoleId).ToHashSet();
            if(add.SetEquals(remove))continue;
            Add(group.First(),"staff.roles_changed","staff","Staff roles changed","Role membership changed for the linked staff account.",true,staff:group.Key,importance:"attention",key:$"{batch}:roles:{group.Key}");
        }
        foreach(var group in entries.Where(e=>e.Entity is RolePermission && e.State is EntityState.Added or EntityState.Deleted).GroupBy(e=>((RolePermission)e.Entity).RoleId))
        {
            if(entries.Any(e=>e.Entity is Role r&&r.Id==group.Key&&(Added(e)||Deleted(e))))continue;
            var add=group.Where(Added).Select(e=>((RolePermission)e.Entity).PermissionId).ToHashSet();
            var remove=group.Where(e=>e.State==EntityState.Deleted).Select(e=>((RolePermission)e.Entity).PermissionId).ToHashSet();
            if(add.SetEquals(remove))continue;
            var roleName=entries.Select(e=>e.Entity).OfType<Role>().FirstOrDefault(r=>r.Id==group.Key)?.Name
                ?? db.Roles.Where(r=>r.Id==group.Key).Select(r=>r.Name).FirstOrDefault() ?? "Removed role";
            string PermissionNames(IEnumerable<Guid> ids){var set=ids.ToArray();return string.Join(", ",db.Permissions.Where(p=>set.Contains(p.Id)).Select(p=>p.Name).OrderBy(n=>n));}
            var details=$"Role {roleName}. Granted: {PermissionNames(add.Except(remove))}. Removed: {PermissionNames(remove.Except(add))}.";
            Add(group.First(),"role.permissions_changed","staff","Role permissions changed",details,true,importance:"attention",key:$"{batch}:permissions:{group.Key}");
        }
        foreach(var e in entries.Where(e=>e.Entity is Inventory))
        {
            var inv=(Inventory)e.Entity;
            // Threshold crossing, not repeated low-stock notifications while already low.
            var before=(int)e.Property("QuantityOnHand").OriginalValue!-(int)e.Property("QuantityReserved").OriginalValue!;
            if(!Added(e)&&inv.Available<=inv.LowStockThreshold&&before>inv.LowStockThreshold)
                Add(e,"inventory.low_stock","inventory","Low stock","Available stock crossed the low-stock threshold.",true,product:inv.ProductId,importance:"attention");
            foreach(var ev in inv.DomainEvents.OfType<InventoryShortageDetectedEvent>())
                Add(e,"inventory.shortage","inventory","Inventory shortage",$"Requested {ev.Requested}; available {ev.Available}.",true,product:inv.ProductId,importance:"attention",key:$"event:{ev.EventId}");
        }
        return rows;
    }
}
