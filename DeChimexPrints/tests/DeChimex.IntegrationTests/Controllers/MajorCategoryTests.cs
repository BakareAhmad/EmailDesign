using System.Net;
using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Categories;
using DeChimex.Application.Features.Categories.Commands;
using DeChimex.Application.Features.Categories.Queries;
using DeChimex.Contracts.Categories;
using DeChimex.Domain.Entities;
using DeChimex.Persistence.Context;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

public class MajorCategoryTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory factory;
    public MajorCategoryTests(CustomWebApplicationFactory factory) => this.factory = factory;
    private static string Name(string prefix = "Major") => prefix + Guid.NewGuid().ToString("N")[..12];
    private HttpClient Browser() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    private async Task<HttpClient> Owner() => await TestSeed.LoginOwnerMvcAsync(factory.Services, Browser);
    private static async Task<Guid> Major(ISender s, string? name = null, string? description = null) => await s.Send(new SaveMajorCategoryCommand(null, name ?? Name(), description));
    private static async Task<CategoryDto> Category(ISender s, Guid? major = null) => await s.Send(new CreateCategoryCommand(new CreateCategoryRequest(Name("Cat"), "Ordering unchanged", MajorCategoryId: major)));
    private static Task<CategoryDto> Move(ISender s, CategoryDto c, Guid? id) => s.Send(new UpdateCategoryCommand(c.Id, new UpdateCategoryRequest(c.Name, c.Description, MajorCategoryId: id, UpdateMajorCategory: true)));

    [Fact]
    public async Task CreateNameOnly_AndDescription_EditPreservesOrder_AndLinks()
    {
        using var scope = factory.Services.CreateScope(); var s = scope.ServiceProvider.GetRequiredService<ISender>();
        var id = await Major(s); var second = await Major(s, description: "Optional description");
        var before = (await s.Send(new GetMajorCategoriesQuery())).Single(x => x.Id == id);
        var name = Name("Renamed"); await s.Send(new SaveMajorCategoryCommand(id, name, "Updated"));
        var after = (await s.Send(new GetMajorCategoriesQuery())).Single(x => x.Id == id);
        after.Name.Should().Be(name); after.Description.Should().Be("Updated"); after.DisplayOrder.Should().Be(before.DisplayOrder);
        var other = (await s.Send(new GetMajorCategoriesQuery())).Single(x => x.Id == second);
        other.Description.Should().Be("Optional description"); other.DisplayOrder.Should().BeGreaterThan(after.DisplayOrder);
        (await Browser().GetAsync($"/products?majorCategoryId={id}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RequiredNamesRejected(string name)
    {
        using var scope = factory.Services.CreateScope(); var s = scope.ServiceProvider.GetRequiredService<ISender>();
        Func<Task> act = () => s.Send(new SaveMajorCategoryCommand(null, name, null));
        await act.Should().ThrowAsync<ValidationAppException>();
    }
    [Fact]
    public async Task LengthBoundaries_AndCaseWhitespaceDuplicateRules()
    {
        using var scope = factory.Services.CreateScope(); var s = scope.ServiceProvider.GetRequiredService<ISender>();
        var name = Name().PadRight(120, 'x'); var id = await Major(s, name, new string('d',1000));
        Func<Task> tooLong = () => Major(s, new string('n',121)); await tooLong.Should().ThrowAsync<ValidationAppException>();
        Func<Task> longDesc = () => Major(s, description: new string('d',1001)); await longDesc.Should().ThrowAsync<ValidationAppException>();
        var shortName = Name(); await Major(s, shortName);
        Func<Task> duplicate = () => Major(s, " " + shortName.ToLowerInvariant() + " "); await duplicate.Should().ThrowAsync<ConflictException>();
        await s.Send(new SaveMajorCategoryCommand(id, name.ToUpperInvariant(), null));
        Func<Task> conflict = () => s.Send(new SaveMajorCategoryCommand(id, shortName, null)); await conflict.Should().ThrowAsync<ConflictException>();
    }
    [Fact]
    public async Task AssignMoveUnassign_PreservesProductOptionsOrder_AndInvalidatesCache()
    {
        using var scope = factory.Services.CreateScope(); var sp = scope.ServiceProvider; var s = sp.GetRequiredService<ISender>(); var db = sp.GetRequiredService<DeChimexDbContext>();
        var a = await Major(s); var b = await Major(s); var c = await Category(s);
        var product = Product.Create(Name("Product"), Name("SKU"), null, c.Id, 100, true, false);
        db.Products.Add(product); await db.SaveChangesAsync();
        var entity = await db.Categories.FindAsync(c.Id);
        entity!.ReplaceOptions(new[] { new CategoryOptionSpec("text", "Your slogan", DeChimex.Domain.Enums.CategoryOptionType.Text, true, null, null, Array.Empty<string>()) });
        await db.SaveChangesAsync();
        var options = entity.Options.Select(o => o.Id).ToArray();
        await s.Send(new GetCategoriesQuery(false)); // warm cache
        await Move(s,c,a); (await s.Send(new GetCategoriesQuery(false))).Single(x=>x.Id==c.Id).MajorCategoryId.Should().Be(a);
        await Move(s,c,b); var result = (await s.Send(new GetCategoriesQuery(false))).Single(x=>x.Id==c.Id);
        result.MajorCategoryId.Should().Be(b); result.DisplayOrder.Should().Be(c.DisplayOrder);
        product.CategoryId.Should().Be(c.Id); entity.Options.Select(o=>o.Id).Should().Equal(options);
        var renamed = Name("NewGroup"); await s.Send(new SaveMajorCategoryCommand(b, renamed, null));
        (await s.Send(new GetCategoriesQuery(false))).Single(x=>x.Id==c.Id).MajorCategoryName.Should().Be(renamed);
        // Legacy update payload must not accidentally clear the newly assigned major.
        await s.Send(new UpdateCategoryCommand(c.Id,new UpdateCategoryRequest(c.Name,c.Description)));
        entity.MajorCategoryId.Should().Be(b);
        await Move(s,c,null); (await s.Send(new GetCategoriesQuery(false))).Single(x=>x.Id==c.Id).MajorCategoryId.Should().BeNull();
    }
    [Fact]
    public async Task Deletion_BlocksEvenInactiveChildren_AndExistingProductCategoryDeletion()
    {
        using var scope = factory.Services.CreateScope(); var s = scope.ServiceProvider.GetRequiredService<ISender>(); var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var major = await Major(s); var c = await Category(s, major);
        await s.Send(new UpdateCategoryCommand(c.Id,new UpdateCategoryRequest(c.Name,null,IsActive:false)));
        Func<Task> delete = () => s.Send(new DeleteMajorCategoryCommand(major)); await delete.Should().ThrowAsync<ConflictException>();
        db.Products.Add(Product.Create(Name("Product"), Name("SKU"), null, c.Id,100,false,false)); await db.SaveChangesAsync();
        Func<Task> deleteCat = () => s.Send(new DeleteCategoryCommand(c.Id)); await deleteCat.Should().ThrowAsync<DeChimex.Domain.Exceptions.BusinessRuleValidationException>();
        await Move(s,c,null); await s.Send(new DeleteMajorCategoryCommand(major));
        (await s.Send(new GetMajorCategoriesQuery())).Should().NotContain(m=>m.Id==major);
        Func<Task> duplicateDelete = () => s.Send(new DeleteMajorCategoryCommand(major)); await duplicateDelete.Should().ThrowAsync<NotFoundException>();
        Func<Task> assignDeleted = () => Move(s,c,major); await assignDeleted.Should().ThrowAsync<ConflictException>();
        Func<Task> unknownParent = () => Category(s,Guid.NewGuid()); await unknownParent.Should().ThrowAsync<ConflictException>();
    }
    [Fact]
    public async Task CategoryNamesRemainGloballyUnique_RegardlessOfMajor()
    {
        using var scope = factory.Services.CreateScope(); var s = scope.ServiceProvider.GetRequiredService<ISender>();
        var a = await Major(s); var b = await Major(s); var c = await Category(s,a);
        Func<Task> duplicate = () => s.Send(new CreateCategoryCommand(new CreateCategoryRequest(c.Name,null,MajorCategoryId:b)));
        await duplicate.Should().ThrowAsync<ConflictException>();
    }
    [Fact]
    public async Task QueryFilters_Intersect_KeepUnassigned_HideInactive_AndSortBeforePaging()
    {
        using var scope = factory.Services.CreateScope(); var sp = scope.ServiceProvider; var s = sp.GetRequiredService<ISender>(); var db = sp.GetRequiredService<DeChimexDbContext>();
        var a = await Major(s); var b = await Major(s); var c1 = await Category(s,a); var c2 = await Category(s,a); var c3 = await Category(s,b); var unassigned = await Category(s);
        var prefix = Name("Filter");
        for(int i=1;i<=25;i++) db.Products.Add(Product.Create(prefix+i,Name("SKU"),null,i%2==0?c1.Id:c2.Id,i*100,false,i==2));
        db.Products.Add(Product.Create(prefix+"other",Name("SKU"),null,c3.Id,500,false,false));
        db.Products.Add(Product.Create(prefix+"unassigned",Name("SKU"),null,unassigned.Id,500,false,false)); await db.SaveChangesAsync();
        var q = sp.GetRequiredService<IProductQueries>();
        var all = await q.SearchAsync(new ProductFilter { MajorCategoryId=a, PageSize=100 },default); all.TotalRecords.Should().Be(25);
        (await q.SearchAsync(new ProductFilter { CategoryId=c1.Id, PageSize=100 },default)).TotalRecords.Should().Be(12);
        (await q.SearchAsync(new ProductFilter { MajorCategoryId=a, CategoryId=c1.Id, PageSize=100 },default)).TotalRecords.Should().Be(12);
        (await q.SearchAsync(new ProductFilter { MajorCategoryId=b, CategoryId=c1.Id },default)).TotalRecords.Should().Be(0);
        (await q.SearchAsync(new ProductFilter { MajorCategoryId=Guid.NewGuid() },default)).TotalRecords.Should().Be(0);
        (await q.SearchAsync(new ProductFilter { CategoryId=unassigned.Id },default)).TotalRecords.Should().Be(1);
        var page = await q.SearchAsync(new ProductFilter { MajorCategoryId=a, SortBy="price", PageSize=20, Page=2 },default);
        page.Items.Select(p=>p.SellingPrice).Should().Equal(2100,2200,2300,2400,2500);
        (await q.SearchAsync(new ProductFilter { MajorCategoryId=a, MinPrice=200, MaxPrice=400, Search=prefix },default)).TotalRecords.Should().Be(3);
        (await q.SearchAsync(new ProductFilter { MajorCategoryId=a, IsFeatured=true },default)).TotalRecords.Should().Be(1);
        var response = await Browser().GetStringAsync($"/products?majorCategoryId={a}&sort=low&page=2");
        var renderedIds = System.Text.RegularExpressions.Regex.Matches(response, "data-product-id=\"([0-9a-f-]{36})\"")
            .Select(m => Guid.Parse(m.Groups[1].Value));
        renderedIds.Should().Equal(page.Items.Select(p=>p.Id), "MVC price sorting must happen before pagination");
        response.Should().Contain($"majorCategoryId={a}");
        await s.Send(new UpdateCategoryCommand(c1.Id,new UpdateCategoryRequest(c1.Name,null,IsActive:false)));
        (await q.SearchAsync(new ProductFilter { MajorCategoryId=a, PageSize=100 },default)).TotalRecords.Should().Be(13);
        (await q.SearchAsync(new ProductFilter { MajorCategoryId=a, ActiveOnly=false, PageSize=100 },default)).TotalRecords.Should().Be(25);
        // MVC sort also must precede pagination, preserving the major selection.
        var html=await Browser().GetStringAsync($"/products?majorCategoryId={a}&sort=low");
        html.Should().Contain($"value=\"{a}\"");
    }
    [Fact]
    public async Task AdminForms_RejectInvalidInputAndCsrf_AndUnknownEdits()
    {
        var client = await Owner();
        var token = TestSeed.ExtractAntiforgeryToken(await client.GetStringAsync("/admin/major-categories/create"));
        var invalid=await client.PostAsync("/admin/major-categories/create",new FormUrlEncodedContent(new Dictionary<string,string>{{"Name"," "},{"__RequestVerificationToken",token}}));
        (await invalid.Content.ReadAsStringAsync()).Should().Contain("required");
        var csrf=await client.PostAsync("/admin/major-categories/create",new FormUrlEncodedContent(new Dictionary<string,string>{{"Name",Name()}}));
        csrf.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync($"/admin/major-categories/edit/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Browser().GetAsync("/admin/major-categories")).StatusCode.Should().Be(HttpStatusCode.Redirect);
    }
    [Fact]
    public async Task CustomerCannotManageMajors()
    {
        var email=Name("customer")+"@example.com"; var password="Customer!2026abc";
        await TestSeed.RegisterCustomerAsync(factory.Services,email,password); await TestSeed.VerifyCustomerAsync(factory.Services,email);
        var client=Browser(); (await TestSeed.LoginMvcAsync(client,email,password)).Should().BeTrue();
        var response=await client.GetAsync("/admin/major-categories/create");
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden,HttpStatusCode.Redirect);
        if(response.StatusCode==HttpStatusCode.Redirect) response.Headers.Location!.ToString().Should().Contain("AccessDenied");
    }
    [Fact]
    public async Task ViewOnlyStaffCanBrowseButCannotMutate()
    {
        var roleName=Name("CatalogReader");
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var permission=await db.Permissions.SingleAsync(p=>p.Name=="categories.view");
            var role=Role.Create(roleName); role.SetPermissions(new[]{permission.Id}); db.Add(role);await db.SaveChangesAsync();
        }
        var email=Name("reader")+"@example.com";var password="Reader!2026abc";
        await TestSeed.CreateStaffUserAsync(factory.Services,email,password,roleName);
        var client=Browser();(await TestSeed.LoginMvcAsync(client,email,password)).Should().BeTrue();
        var index=await client.GetAsync("/admin/major-categories"); index.StatusCode.Should().Be(HttpStatusCode.OK);
        (await index.Content.ReadAsStringAsync()).Should().NotContain("New major category");
        var denied=await client.GetAsync("/admin/major-categories/create");
        denied.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden,HttpStatusCode.Redirect);
        if(denied.StatusCode==HttpStatusCode.Redirect) denied.Headers.Location!.ToString().Should().Contain("AccessDenied");
        var post=await client.PostAsync("/admin/major-categories/create",new FormUrlEncodedContent(new Dictionary<string,string>{{"Name","Forbidden"}}));
        post.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden,HttpStatusCode.Redirect);
    }
    [Fact]
    public async Task InvalidFilterIsRejectedRatherThanSilentlyShowingAllProducts()
    {
        (await Browser().GetAsync("/products?majorCategoryId=invalid")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

}
