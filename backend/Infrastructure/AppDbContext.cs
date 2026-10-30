using Domain;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<Rubric> Rubrics => Set<Rubric>();
    public DbSet<InterviewTemplate> InterviewTemplates => Set<InterviewTemplate>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Turn> Turns => Set<Turn>();
    public DbSet<Evaluation> Evaluations => Set<Evaluation>();
    public DbSet<DeliveryMetric> DeliveryMetrics => Set<DeliveryMetric>();
    public DbSet<PracticePlan> PracticePlans => Set<PracticePlan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Role).HasMaxLength(20);
            e.Property(x => x.Plan).HasMaxLength(20);
            e.Property(x => x.PasswordHash).HasMaxLength(500);
            e.Property(x => x.GoogleSubject).HasMaxLength(100);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasOne(x => x.Org).WithMany().HasForeignKey(x => x.OrgId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Profile).WithOne().HasForeignKey<Profile>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Organization>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Type).HasMaxLength(20);
        });

        b.Entity<Profile>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.ResumeBlobUrl).HasMaxLength(1000);
            e.Property(x => x.TargetRole).HasMaxLength(200);
            e.Property(x => x.ExperienceLevel).HasMaxLength(50);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.ToTable(t => t.HasCheckConstraint("CK_Profiles_ParsedResumeJson", "[ParsedResumeJson] IS NULL OR ISJSON([ParsedResumeJson]) = 1"));
        });

        b.Entity<Rubric>(e =>
        {
            e.Property(x => x.Category).HasMaxLength(30);
            e.HasIndex(x => new { x.Category, x.Version }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_Rubrics_DimensionsJson", "ISJSON([DimensionsJson]) = 1"));
        });

        b.Entity<InterviewTemplate>(e =>
        {
            e.Property(x => x.Category).HasMaxLength(30);
            e.Property(x => x.Difficulty).HasMaxLength(10);
            e.Property(x => x.Persona).HasMaxLength(300);
            e.HasIndex(x => new { x.Category, x.Difficulty }).IsUnique();
            e.HasOne(x => x.Rubric).WithMany().HasForeignKey(x => x.RubricId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Question>(e =>
        {
            e.Property(x => x.Category).HasMaxLength(30);
            e.Property(x => x.Difficulty).HasMaxLength(10);
            e.Property(x => x.Text).HasMaxLength(500);
            e.Property(x => x.Tags).HasMaxLength(300);
            e.Property(x => x.Embedding).HasColumnType("vector(1536)");
            e.HasIndex(x => new { x.Category, x.Text }).IsUnique();
            e.HasIndex(x => new { x.Category, x.Difficulty });
        });

        b.Entity<Session>(e =>
        {
            e.Property(x => x.Mode).HasMaxLength(10);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.FailureReason).HasMaxLength(500);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => new { x.UserId, x.StartedAt }).IsDescending(false, true);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Template).WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_Sessions_PlanJson", "ISJSON([PlanJson]) = 1"));
        });

        b.Entity<Turn>(e =>
        {
            e.Property(x => x.Speaker).HasMaxLength(20);
            e.HasIndex(x => new { x.SessionId, x.Seq }).IsUnique();
            e.HasOne<Session>().WithMany(x => x.Turns).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Evaluation>(e =>
        {
            e.HasKey(x => x.SessionId);
            e.Property(x => x.ModelVersion).HasMaxLength(100);
            e.HasOne<Session>().WithOne().HasForeignKey<Evaluation>(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Evaluations_ScoresJson", "ISJSON([ScoresJson]) = 1");
                t.HasCheckConstraint("CK_Evaluations_FeedbackJson", "ISJSON([FeedbackJson]) = 1");
            });
        });

        b.Entity<DeliveryMetric>(e =>
        {
            e.HasKey(x => x.SessionId);
            e.HasOne<Session>().WithOne().HasForeignKey<DeliveryMetric>(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PracticePlan>(e =>
        {
            e.Property(x => x.Skill).HasMaxLength(200);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => new { x.UserId, x.Status });
            // No FK to Sessions: SQL Server rejects a second cascade path from Users.
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("CK_PracticePlans_DrillsJson", "ISJSON([DrillsJson]) = 1"));
        });

        b.Entity<Subscription>(e =>
        {
            e.Property(x => x.Plan).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => new { x.UserId, x.Status });
            // Billing records outlive account deletion (tax retention), so unlink rather than cascade.
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrgId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Payment>(e =>
        {
            e.Property(x => x.Provider).HasMaxLength(20);
            e.Property(x => x.ProviderRef).HasMaxLength(100);
            e.Property(x => x.ProviderPaymentId).HasMaxLength(100);
            e.Property(x => x.Status).HasMaxLength(20);
            e.HasIndex(x => x.ProviderRef).IsUnique();
            e.HasOne(x => x.Subscription).WithMany(x => x.Payments).HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
