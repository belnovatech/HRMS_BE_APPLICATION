# Application feature folders

Create vertical slices under this directory for:

- Authentication
- Organization
- Employees
- Attendance
- Leave
- Payroll
- Recruitment
- RolesPermissions
- Reports
- Documents
- Biometric
- Notifications
- Settings
- Support
- Holidays
- Announcements
- Requests

Keep commands and queries independent. API controllers translate HTTP into application requests; business
rules belong in Domain or Application, never in controllers or Infrastructure.
