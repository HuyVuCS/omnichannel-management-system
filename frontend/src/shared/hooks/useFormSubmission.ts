import { App as AntApp, type FormInstance } from 'antd';
import { useCallback, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { applyProblemToForm, type ApplyProblemOptions } from '@/shared/api/problemDetails';

interface FormSubmissionOptions<Values> extends ApplyProblemOptions {
  /** The API call (an RTK Query mutation, unwrapped). */
  action: (values: Values) => Promise<unknown>;
  /** Translation key of the success toast. */
  successKey: string;
  /** After success (close the dialog). */
  onSuccess: () => void;
}

/**
 * The submit flow every form dialog shares: call the API, on success show a toast and close; on failure put validation
 * errors on their fields (`applyProblemToForm`) and keep the rest for a form-level alert. Network errors and 5xx are
 * reported globally.
 */
export function useFormSubmission<Values>(
  form: FormInstance<Values>,
  { action, successKey, onSuccess, codeFields }: FormSubmissionOptions<Values>,
) {
  const { t } = useTranslation();
  const { message } = AntApp.useApp();
  const [formErrors, setFormErrors] = useState<string[]>([]);

  const submit = useCallback(
    async (values: Values) => {
      setFormErrors([]);
      try {
        await action(values);
        message.success(t(successKey));
        onSuccess();
      } catch (error) {
        setFormErrors(applyProblemToForm(form, error, t, { codeFields }));
      }
    },
    [action, codeFields, form, message, onSuccess, successKey, t],
  );

  const clearErrors = useCallback(() => setFormErrors([]), []);

  return { submit, formErrors, clearErrors };
}
