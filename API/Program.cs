using API.Middleware;
using API.SignalR;
using Application.Activities.Queries;
using Application.Activities.Validators;
using Application.Core;
using Application.Interfaces;
using CloudinaryDotNet;
using Domain;
using FluentValidation;
using Infrastructure.Email;
using Infrastructure.Photos;
using Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using Persistence;
using Resend;
using System.Runtime.Intrinsics.X86;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
//It makes all actions in all controllers require login by default.
//There are no open (Anonymous) endpoints unless you explicitly add [AllowAnonymous]
builder.Services.AddControllers(opt => 
{
    var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    opt.Filters.Add(new AuthorizeFilter(policy));
});

builder.Services.AddDbContext<AppDbContext>(opt =>
{
    opt.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
});
builder.Services.AddCors();
builder.Services.AddSignalR();

//implements the Mediator pattern
//GetActivityList is indeed a use case. That line in Program.cs is registering one use case, but you don’t need to repeat it for every other use case.
//    If you register MediatR once for one use case, it will automatically scan and register all other use cases (handlers) in the same assembly.
builder.Services.AddMediatR(x => {
    x.RegisterServicesFromAssemblyContaining<GetActivityList.Handler>();

   // We defined a custom Pipeline Behavior(called ValidationBehavior) that contains the logic for FluentValidation
    x.AddOpenBehavior(typeof(ValidationBehavior<,>));
});

//they make it easy to send emails through Resend without dealing with HttpClient or API details directly.
builder.Services.AddHttpClient<ResendClient>();
builder.Services.Configure<ResendClientOptions>(opt => 
{
    opt.ApiToken = builder.Configuration["Resend:ApiToken"]!;
});
builder.Services.AddTransient<IResend, ResendClient>();


builder.Services.AddTransient<IEmailSender<User>, EmailSender>();


//IUserAccessor: Reads the logged -in user’s info from HttpContext.User (claims like UserId, Email, Role).
//IPhotoService: Manages photos(upload/delete, e.g., Cloudinary). If needed, uses IUserAccessor to identify the acting user.
builder.Services.AddScoped<IUserAccessor, UserAccessor>();
builder.Services.AddScoped<IPhotoService, PhotoService>();


//It registers AutoMapper and scans the assembly containing MappingProfiles, loading all mapping profiles in that assembly.
//After this single call, you can inject IMapper and map DTOs ? entities as defined in those profiles—no per-map registration needed.
builder.Services.AddAutoMapper(typeof(MappingProfiles).Assembly);


//AddValidatorsFromAssemblyContaining<CreateActivityValidator>() does not register handlers;
//it registers FluentValidation validators.
//It scans the assembly that contains CreateActivityValidator and registers all validators there.
//So one call is enough if all your validators are in that assembly.
//MediatR’s ValidationBehavior will automatically run those validators before the handlers.
builder.Services.AddValidatorsFromAssemblyContaining<CreateActivityValidator>();


builder.Services.AddTransient<ExceptionMiddleware>();


//Registers ASP.NET Identity for your User model and exposes built-in auth endpoints.
//Requires unique emails and confirmed email before sign-in.
//Enables roles support.
//Stores users/roles via EF Core in your AppDbContext.
builder.Services.AddIdentityApiEndpoints<User>(opt => 
{
    opt.User.RequireUniqueEmail = true;
    opt.SignIn.RequireConfirmedEmail = true;
})
.AddRoles<IdentityRole>()
.AddEntityFrameworkStores<AppDbContext>();




//Defines an authorization policy named IsActivityHost.
//When you apply [Authorize(Policy = "IsActivityHost")] on an action, access is allowed only if the current user is the host of that Activity.
//The actual check is performed by IsHostRequirementHandler.
builder.Services.AddAuthorization(opt =>
{
    opt.AddPolicy("IsActivityHost", policy => 
    {
        policy.Requirements.Add(new IsHostRequirement());
    });
});
builder.Services.AddTransient<IAuthorizationHandler, IsHostRequirementHandler>();
builder.Services.Configure<CloudinarySettings>(builder.Configuration
    .GetSection("CloudinarySettings"));

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseMiddleware<ExceptionMiddleware>();
app.UseCors(x => x.AllowAnyHeader().AllowAnyMethod()
    .AllowCredentials()
    .WithOrigins("http://localhost:3000", "https://localhost:3000"));

app.UseAuthentication();
app.UseAuthorization();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();
app.MapGroup("api").MapIdentityApi<User>(); // api/login
app.MapHub<CommentHub>("/comments");
app.MapFallbackToController("Index", "Fallback");

using var scope = app.Services.CreateScope();
var services = scope.ServiceProvider;

try
{
    var context = services.GetRequiredService<AppDbContext>();
    var userManager = services.GetRequiredService<UserManager<User>>();
    await context.Database.MigrateAsync();
    await DbInitializer.SeedData(context, userManager);
}
catch (Exception ex)
{
    var logger = services.GetRequiredService<ILogger<Program>>();
    logger.LogError(ex, "An error occurred during migration.");
}

app.Run();
