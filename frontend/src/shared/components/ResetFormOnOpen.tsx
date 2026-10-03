import { Form } from 'antd';
import { useLayoutEffect } from 'react';

interface ResetFormOnOpenProps {
  /** `useOpenCount` of the dialog: a new number for every opening, also of the same record. */
  openCount: number;
}

/**
 * Makes a dialog's form show its current `initialValues` every time the dialog opens. Rendered **inside** the dialog's
 * `<Form>` (it renders nothing itself).
 *
 * The form instance outlives the dialog's content (`destroyOnHidden`), and a mounted form never takes `initialValues`
 * again by itself. Two cases:
 *
 * - The dialog is reopened before its close animation has finished (a quick second click, the keyboard, a slow
 *   machine): the form is still mounted and holds the previous values - for an edit dialog the values of another record
 *   under the new record's id, or what was typed and cancelled. The reset here replaces them.
 * - The dialog was closed completely: its form was destroyed and is mounted again. The dialog's `Form` has
 *   `clearOnDestroy`, so the instance is empty by then; the reset here confirms the new `initialValues`.
 *
 * Why inside the form: the dialog library can render the content of a closing dialog one commit later than the
 * component that owns it. A reset in the owner's effect then runs before the `Form` has received the new
 * `initialValues` and fills the form with the previous record (task 017 finding). This component renders in the same
 * commit as the `Form` around it, and its effect runs after that render - the reset always uses the values the form
 * was just given. A layout effect, so the previous values are never painted.
 */
export function ResetFormOnOpen({ openCount }: ResetFormOnOpenProps) {
  const form = Form.useFormInstance();

  useLayoutEffect(() => {
    form.resetFields();
  }, [form, openCount]);

  return null;
}
