export const authEn = {
  login: {
    title: 'AppFramework Platform',
    subtitle: 'Shared services platform · Admin console',
    returnHint: 'You will be returned to the page you tried to open',
    userName: 'Username',
    password: 'Password',
    submit: 'Sign in',
    userNameRequired: 'Enter your username',
    passwordRequired: 'Enter your password',
    failed: 'Sign-in failed',
    mustChangePassword: 'This is your first sign-in or your password was reset. Change it first.',
  },
  changePassword: {
    title: 'Change password',
    description:
      'You must change the password after first sign-in or an admin reset. Other sessions will be revoked.',
    current: 'Current password',
    next: 'New password',
    confirm: 'Confirm new password',
    submit: 'Save new password',
    currentRequired: 'Enter your current password',
    nextRequired: 'Enter a new password',
    confirmRequired: 'Enter the new password again',
    mismatch: 'The two new passwords do not match',
    success: 'Password changed. Sign in again with the new password.',
  },
  denied: {
    title: 'You do not have access to this page',
    description:
      'Your account lacks the permission code required by this page. Ask an administrator to add it under Identity & access → Roles.',
    qrDescription:
      'Approving QR sign-in requires identity.qr.approve. Field engineers have it by default; temporary sessions (from key or QR sign-in) cannot approve other sign-ins.',
    backHome: 'Back to overview',
  },
};
